using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace WeatherUdpSender
{
    public class MainForm : Form
    {
        // 8个城市：名称、weather.com.cn城市代码
        private static readonly (string Name, string Code)[] Cities = new[]
        {
            ("固阳",       "101080205"),
            ("东胜",       "101080713"),
            ("达拉特旗",   "101080703"),
            ("北京",       "101010100"),
            ("达茂旗",     "101080206"),
            ("鄂尔多斯",   "101080701"),
            ("呼和浩特",   "101080101"),
            ("银川",       "101170101"),
            ("赛汗塔拉城中草原", "101080201"),
            ("南海湿地景区",     "101080201"),
            ("包头博物馆",       "101080201"),
            ("希拉穆仁草原",     "101080206"),
            ("春坤山生态旅游区", "101080205"),
            ("白云鄂博国家矿山公园", "101080202"),
        };

        // 配置文件路径
        private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WeatherUdpSender");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        private TextBox txtIp = null!;
        private TextBox txtPort = null!;
        private NumericUpDown numInterval = null!;
        private CheckBox chkAutoStart = null!;
        private Button btnStart = null!;
        private ListBox lstLog = null!;
        private Label lblStatus = null!;

        private System.Threading.Timer? _timer;
        private System.Threading.Timer? _retryTimer;
        private UdpClient? _udpClient;
        private volatile bool _running;
        private int _sendCount;
        private int _fetchRetryCount = 0;
        private const int MAX_RETRIES = 5;
        private bool _isAutoStarting = false;

        // 系统托盘
        private NotifyIcon? _trayIcon;
        private CheckBox chkMinimizeToTray = null!;
        private bool _startToTray = false;

        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestHeaders = { { "Referer", "http://www.weather.com.cn/" } }
        };

        public MainForm()
        {
            InitUI();
            LoadConfig();
        }

        private void InitUI()
        {
            this.Text = "多城市天气UDP推送";
            this.Size = new System.Drawing.Size(860, 600);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("WeatherUdpSender.app.ico");
                if (stream != null)
                    this.Icon = new System.Drawing.Icon(stream);
            }
            catch { }

            int y = 12;

            var lblInfo = new Label
            {
                Text = $"14城市实时天气 | UDP推送 | 实时:weather.com.cn 预报:wttr.in | 固阳 东胜 达拉特旗 北京 达茂旗 鄂尔多斯 呼和浩特 银川 赛汗塔拉 南海湿地 包头博物馆 希拉穆仁 春坤山 白云鄂博",
                Left = 12, Top = y, Width = 820, Height = 18,
                ForeColor = System.Drawing.Color.FromArgb(100, 100, 100)
            };
            y += 24;

            var lblIp = new Label { Text = "目标IP:", Left = 12, Top = y + 4, Width = 55, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
            txtIp = new TextBox { Left = 72, Top = y, Width = 140, Text = "127.0.0.1" };
            var lblPort = new Label { Text = "端口:", Left = 222, Top = y + 4, Width = 40 };
            txtPort = new TextBox { Left = 266, Top = y, Width = 70, Text = "9999" };
            var lblInt = new Label { Text = "间隔(分):", Left = 350, Top = y + 4, Width = 60 };
            numInterval = new NumericUpDown { Left = 414, Top = y, Width = 55, Minimum = 1, Maximum = 120, Value = 10 };
            chkAutoStart = new CheckBox { Text = "开机自动启动", Left = 490, Top = y + 3, Width = 110 };
            chkMinimizeToTray = new CheckBox { Text = "最小化到托盘", Left = 610, Top = y + 3, Width = 110 };
            y += 36;

            btnStart = new Button { Text = "启动", Left = 12, Top = y, Width = 80, Height = 30 };
            btnStart.Click += BtnStart_Click;
            var btnOnce = new Button { Text = "立即获取", Left = 100, Top = y, Width = 100, Height = 30 };
            btnOnce.Click += (_, _) => FetchAndSend();
            var btnClear = new Button { Text = "清空日志", Left = 208, Top = y, Width = 80, Height = 30 };
            btnClear.Click += (_, _) => lstLog.Items.Clear();
            y += 38;

            lblStatus = new Label { Text = "状态：已停止", Left = 12, Top = y, Width = 400, ForeColor = System.Drawing.Color.Gray };
            y += 22;

            var lblFormat = new Label
            {
                Text = "UDP格式: 城市名,温度XX°C,体感XX°C,绝对湿度XXg/m³,空气质量:XX,紫外线XX(XX),天气,风向风力,时间,明天|天气|最高|最低;后天|天气|最高|最低;大后天|天气|最高|最低",
                Left = 12, Top = y, Width = 820, Height = 32,
                AutoSize = false,
                ForeColor = System.Drawing.Color.FromArgb(80, 130, 80)
            };
            y += 36;

            lstLog = new ListBox
            {
                Left = 12, Top = y, Width = 820, Height = 310,
                Font = new System.Drawing.Font("Consolas", 9f)
            };

            this.Controls.AddRange(new Control[]
            {
                lblInfo, lblIp, txtIp, lblPort, txtPort, lblInt, numInterval,
                chkAutoStart, chkMinimizeToTray, btnStart, btnOnce, btnClear, lblStatus, lblFormat, lstLog
            });

            InitTrayIcon();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<ConfigData>(json);
                    if (cfg != null)
                    {
                        if (!string.IsNullOrEmpty(cfg.Ip)) txtIp.Text = cfg.Ip;
                        if (cfg.Port > 0) txtPort.Text = cfg.Port.ToString();
                        if (cfg.Interval > 0) numInterval.Value = cfg.Interval;
                        chkAutoStart.Checked = cfg.AutoStart;
                        chkMinimizeToTray.Checked = cfg.MinimizeToTray;
                    }
                }
            }
            catch { }

            if (chkAutoStart.Checked && chkMinimizeToTray.Checked)
            {
                _startToTray = true;
                if (_trayIcon != null) _trayIcon.Visible = true;
            }

            if (chkAutoStart.Checked)
            {
                _isAutoStarting = true;
            }
        }

        private void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var cfg = new ConfigData
                {
                    Ip = txtIp.Text.Trim(),
                    Port = int.TryParse(txtPort.Text.Trim(), out int p) ? p : 9999,
                    Interval = (int)numInterval.Value,
                    AutoStart = chkAutoStart.Checked,
                    MinimizeToTray = chkMinimizeToTray.Checked
                };
                string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        private void BtnStart_Click(object? sender, EventArgs e)
        {
            if (_running) Stop(); else Start();
        }

        private void Start()
        {
            if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535)
            { MessageBox.Show("端口范围1-65535"); return; }
            if (!IPAddress.TryParse(txtIp.Text, out _))
            { MessageBox.Show("IP格式错误"); return; }

            _running = true;
            _sendCount = 0;
            _fetchRetryCount = 0;
            btnStart.Text = "停止";
            lblStatus.Text = "状态：运行中";
            lblStatus.ForeColor = System.Drawing.Color.Green;
            _udpClient = new UdpClient();
            int intervalMin = (int)numInterval.Value;

            _isAutoStarting = false;
            _timer = new System.Threading.Timer(_ => FetchAndSend(), null, 0, intervalMin * 60 * 1000);

            SaveConfig();
        }

        private void Stop()
        {
            _running = false;
            _timer?.Dispose(); _timer = null;
            _retryTimer?.Dispose(); _retryTimer = null;
            _udpClient?.Close(); _udpClient = null;
            btnStart.Text = "启动";
            lblStatus.Text = "状态：已停止";
            lblStatus.ForeColor = System.Drawing.Color.Gray;
        }

        private void FetchAndSend()
        {
            bool singleShot = !_running;
            if (singleShot) _udpClient = new UdpClient();

            int ok = 0, fail = 0;
            try
            {
                string ip = this.Invoke(() => txtIp.Text.Trim());
                int port = int.Parse(this.Invoke(() => txtPort.Text.Trim()));
                var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);

                for (int i = 0; i < Cities.Length; i++)
                {
                    var (name, code) = Cities[i];
                    try
                    {
                        var w = FetchCityWeather(name, code);
                        // 绝对湿度 g/m³
                        string absHum = w.AbsHumidity > 0 ? $"{w.AbsHumidity:F1}g/m³" : "--";
                        string msg = $"{w.Name},温度{w.Temp:F1}°C,体感{w.Feels:F1}°C,绝对湿度{absHum},空气质量:{w.Aqi},紫外线{w.UvIndex}({w.UvLevel}),{w.Desc},{w.WindForce},{w.Time},{w.Forecast}";
                        byte[] bytes = Encoding.GetEncoding("GBK").GetBytes(msg);
                        _udpClient!.Send(bytes, bytes.Length, endpoint);
                        _sendCount++;
                        ok++;
                        string fc = string.IsNullOrEmpty(w.Forecast) ? "" : $" | 预报:{w.Forecast}";
                        Log($"  {name}: {w.Temp:F1}°C 体感{w.Feels:F1}°C 绝对湿度{absHum} 空气质量:{w.Aqi} 紫外线{w.UvIndex}({w.UvLevel}) {w.Desc} {w.WindForce}{fc}");
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Log($"  ✗ {name}: {ex.Message}");
                    }
                }

                Log($"✓ {ok}/{Cities.Length}城市推送完成, 累计{_sendCount}条 → {ip}:{port}");

                if (ok > 0) _fetchRetryCount = 0;
            }
            catch (Exception ex)
            {
                Log($"✗ 错误: {ex.Message}");
            }
            finally
            {
                if (singleShot) { _udpClient?.Close(); _udpClient = null; }
            }

            if (_running && ok == 0 && fail > 0 && _fetchRetryCount < MAX_RETRIES)
            {
                _fetchRetryCount++;
                int retrySec = _fetchRetryCount * 30;
                Log($"⚠ 网络可能未就绪，{retrySec}秒后自动重试({_fetchRetryCount}/{MAX_RETRIES})...");
                _retryTimer?.Dispose();
                _retryTimer = new System.Threading.Timer(_ => FetchAndSend(), null, retrySec * 1000, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 获取单个城市的天气数据
        /// 数据源: d1.weather.com.cn/sk_2d/ (实时+AQI) + m.weather.com.cn/data/ (紫外线)
        /// </summary>
        private CityData FetchCityWeather(string name, string code)
        {
            var result = new CityData { Name = name };

            // 1. 获取实时天气数据 (含温度、湿度、AQI、风向风力等)
            string skUrl = $"http://d1.weather.com.cn/sk_2d/{code}.html?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            string skJs = _http.GetStringAsync(skUrl).GetAwaiter().GetResult();
            string skJson = ExtractDataSK(skJs);
            using var skDoc = JsonDocument.Parse(skJson);

            double temp = double.TryParse(skDoc.RootElement.TryGetProperty("temp", out var te) ? te.GetString() : "", out var tv) ? tv : -999;
            double sd = double.TryParse(skDoc.RootElement.TryGetProperty("sd", out var sdEl) ? sdEl.GetString()?.Replace("%", "") : "", out var sdv) ? sdv : 0;
            string wd = (skDoc.RootElement.TryGetProperty("WD", out var wdEl) ? wdEl.GetString() : "") ?? "";
            string ws = (skDoc.RootElement.TryGetProperty("WS", out var wsEl) ? wsEl.GetString() : "") ?? "";
            string weather = (skDoc.RootElement.TryGetProperty("weather", out var we) ? we.GetString() : "") ?? "";
            string aqi = (skDoc.RootElement.TryGetProperty("aqi", out var a) ? a.GetString() : "") ?? "";
            string aqiPm25 = (skDoc.RootElement.TryGetProperty("aqi_pm25", out var ap) ? ap.GetString() : "") ?? "";
            string time = (skDoc.RootElement.TryGetProperty("time", out var ti) ? ti.GetString() : "") ?? "";
            if (string.IsNullOrEmpty(time))
                time = DateTime.Now.ToString("yyyyMMddHHmm");

            result.Temp = temp;
            result.Rh = sd;
            result.Desc = weather;
            result.WindForce = $"{wd}{ws}";
            result.Time = time;
            result.Aqi = !string.IsNullOrEmpty(aqi) ? $"{aqi}" : (!string.IsNullOrEmpty(aqiPm25) ? $"PM2.5:{aqiPm25}" : "--");

            // 5. 获取三天预报 + 紫外线
            try
            {
                var (forecast, uv) = FetchWttrData(name);
                result.Forecast = forecast;
                result.UvIndex = uv;
                result.UvLevel = uv;
            }
            catch (Exception ex)
            {
                Log($"  ⚠ {name} 预报获取失败: {ex.Message}");
                result.Forecast = "";
            }

            // 2. 计算体感温度 (Heat Index)
            result.Feels = CalcFeelsLike(temp, sd);

            // 3. 计算绝对湿度 (g/m³)
            result.AbsHumidity = CalcAbsHumidity(temp, sd);

            return result;
        }

        /// <summary>
        /// 从 wttr.in 获取未来三天预报
        /// 格式: 明天|天气|最高|最低;后天|天气|最高|最低;大后天|天气|最高|最低
        /// </summary>
        private static readonly Dictionary<int, string> WttrCode = new()
        {
            {113, "晴"}, {116, "多云"}, {119, "阴"}, {122, "阴"},
            {143, "雾"}, {176, "阵雨"}, {179, "雨夹雪"}, {182, "雨夹雪"},
            {185, "冻雨"}, {200, "雷阵雨"}, {227, "暴雪"}, {230, "暴雪"},
            {248, "雾"}, {260, "雾"},
            {263, "小雨"}, {266, "小雨"}, {293, "小雨"}, {296, "小雨"},
            {299, "中雨"}, {302, "中雨"}, {305, "大雨"}, {308, "大雨"},
            {311, "冻雨"}, {314, "冻雨"}, {317, "雨夹雪"}, {320, "雨夹雪"},
            {323, "小雪"}, {326, "小雪"}, {329, "中雪"}, {332, "中雪"},
            {335, "大雪"}, {338, "大雪"}, {350, "冰雹"},
            {353, "阵雨"}, {356, "中雨"}, {359, "大雨"},
            {362, "雨夹雪"}, {365, "雨夹雪"}, {368, "小雪"}, {371, "大雪"},
            {374, "冰雹"}, {377, "冰雹"}, {386, "雷阵雨"}, {389, "雷阵雨"},
            {392, "雷阵雨"}, {395, "大雪"},
        };

        private static (string Forecast, string UvIndex) FetchWttrData(string cityName)
        {
            // wttr.in 对部分城市名需要特殊处理
            string wcity = cityName switch
            {
                "达茂旗" => "达尔罕茂明安联合旗",
                _ => cityName
            };

            using var fc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string url = $"https://wttr.in/{Uri.EscapeDataString(wcity)}?format=j1";
            string json = fc.GetStringAsync(url).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);

            // 紫外线指数
            string uvIndex = "--";
            try
            {
                var cc = doc.RootElement.GetProperty("current_condition")[0];
                uvIndex = cc.TryGetProperty("uvIndex", out var uv) ? uv.GetString() ?? "--" : "--";
            }
            catch { }

            var days = doc.RootElement.GetProperty("weather");
            var forecasts = new List<string>();
            string[] labels = { "明天", "后天", "大后天" };
            int idx = 0;

            foreach (var day in days.EnumerateArray())
            {
                if (forecasts.Count >= 3) break;
                if (idx == 0) { idx++; continue; } // 跳过今天

                int maxTemp = (int)Math.Round(double.Parse(day.GetProperty("maxtempC").GetString()!));
                int minTemp = (int)Math.Round(double.Parse(day.GetProperty("mintempC").GetString()!));
                int code = int.Parse(day.GetProperty("hourly")[0].GetProperty("weatherCode").GetString()!);
                string desc = WttrCode.TryGetValue(code, out var d) ? d : "未知";

                forecasts.Add($"{labels[forecasts.Count]}|{desc}|{maxTemp}|{minTemp}");
                idx++;
            }

            return (string.Join(";", forecasts), uvIndex);
        }

        /// <summary>
        /// 从 sk_2d 的 JSONP 响应中提取 JSON
        /// 格式: var dataSK={...}
        /// </summary>
        private static string ExtractDataSK(string js)
        {
            int eqIdx = js.IndexOf("= {");
            if (eqIdx < 0) eqIdx = js.IndexOf("={");
            if (eqIdx < 0) throw new Exception("JSONP格式异常");

            int jsonStart = js.IndexOf('{', eqIdx);
            if (jsonStart < 0) throw new Exception("找不到JSON起始{");

            int depth = 0;
            int jsonEnd = -1;
            for (int k = jsonStart; k < js.Length; k++)
            {
                if (js[k] == '{') depth++;
                else if (js[k] == '}') depth--;
                if (depth == 0) { jsonEnd = k; break; }
            }
            if (jsonEnd < 0) throw new Exception("找不到JSON结束}");

            return js.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        /// <summary>
        /// 计算体感温度 (Heat Index / 炎热指数)
        /// 公式来源: NOAA National Weather Service
        /// 仅当温度>=27°C且湿度>=40%时使用Heat Index；否则返回实际温度
        /// </summary>
        private static double CalcFeelsLike(double tempC, double rh)
        {
            if (tempC < -900 || rh < 0) return tempC;

            // 温度低于27°C或湿度低于40%时，体感≈实际温度
            if (tempC < 27 || rh < 40) return tempC;

            double T = tempC * 9.0 / 5.0 + 32; // 转华氏度
            double R = rh;

            double HI = -42.379
                + 2.04901523 * T
                + 10.14333127 * R
                - 0.22475541 * T * R
                - 0.00683783 * T * T
                - 0.05481717 * R * R
                + 0.00122874 * T * T * R
                + 0.00085282 * T * R * R
                - 0.00000199 * T * T * R * R;

            return (HI - 32) * 5.0 / 9.0; // 转回摄氏度
        }

        /// <summary>
        /// 计算绝对湿度 (g/m³)
        /// 公式: 绝对湿度 = (相对湿度/100) × 饱和水汽压 / (461.5 × (T+273.15))
        /// 饱和水汽压 = 6.112 × exp(17.67×T/(T+243.5)) × 100 (Pa)
        /// </summary>
        private static double CalcAbsHumidity(double tempC, double rh)
        {
            if (tempC < -900 || rh <= 0) return -1;

            double T = tempC + 273.15; // 开尔文
            double es = 6.112 * Math.Exp(17.67 * tempC / (tempC + 243.5)) * 100; // 饱和水汽压 Pa
            double e = (rh / 100.0) * es; // 实际水汽压 Pa
            double absHum = e / (461.5 * T) * 1000; // g/m³

            return absHum;
        }

        private void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            if (lstLog.InvokeRequired)
                lstLog.Invoke(new Action(() => { lstLog.Items.Insert(0, line); if (lstLog.Items.Count > 500) lstLog.Items.RemoveAt(lstLog.Items.Count - 1); }));
            else
            { lstLog.Items.Insert(0, line); if (lstLog.Items.Count > 500) lstLog.Items.RemoveAt(lstLog.Items.Count - 1); }
        }

        private void InitTrayIcon()
        {
            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "多城市天气UDP推送";

            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream("WeatherUdpSender.app.ico");
                if (stream != null)
                    _trayIcon.Icon = new System.Drawing.Icon(stream);
            }
            catch { }

            if (_trayIcon.Icon == null)
            {
                try { _trayIcon.Icon = System.Drawing.SystemIcons.Information; }
                catch { }
            }

            _trayIcon.DoubleClick += (_, _) => ShowFromTray();

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
            menu.Items.Add("-");
            menu.Items.Add("退出", null, (_, _) =>
            {
                _trayIcon.Visible = false;
                SaveConfig();
                Stop();
                Application.Exit();
            });
            _trayIcon.ContextMenuStrip = menu;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_isAutoStarting)
            {
                Start();
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (_startToTray && !IsHandleCreated)
            {
                CreateHandle();
                return;
            }
            base.SetVisibleCore(value);
        }

        private void ShowFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (chkMinimizeToTray != null && chkMinimizeToTray.Checked && this.WindowState == FormWindowState.Minimized && _trayIcon != null)
            {
                this.Hide();
                _trayIcon.Visible = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (chkMinimizeToTray != null && chkMinimizeToTray.Checked && e.CloseReason == CloseReason.UserClosing && _trayIcon != null)
            {
                e.Cancel = true;
                this.Hide();
                _trayIcon.Visible = true;
                return;
            }

            _trayIcon?.Dispose();
            SaveConfig();
            Stop();
            base.OnFormClosing(e);
        }
    }

    public class CityData
    {
        public string Name = "";
        public double Temp = -999.9;
        public double Feels = -999.9;
        public double Rh = -1;
        public double AbsHumidity = -1;
        public string Desc = "";
        public string Aqi = "";
        public string UvIndex = "";
        public string UvLevel = "";
        public string WindForce = "";
        public string Time = "";
        public string Forecast = ""; // 三天预报: 明天|天气|最高|最低;后天|天气|最高|最低;大后天|天气|最高|最低
    }

    public class ConfigData
    {
        public string Ip { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 9999;
        public int Interval { get; set; } = 10;
        public bool AutoStart { get; set; } = false;
        public bool MinimizeToTray { get; set; } = false;
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ApplicationConfiguration.Initialize();
            Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.ToString(), "未处理异常");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => MessageBox.Show(e.ExceptionObject?.ToString(), "致命错误");
            Application.Run(new MainForm());
        }
    }
}