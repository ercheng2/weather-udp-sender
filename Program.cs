using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace WeatherUdpSender
{
    public class MainForm : Form
    {
        // 6个景点：名称、经度、纬度
        private static readonly (string Name, double Lon, double Lat)[] Spots = new[]
        {
            ("白云山风景名胜区", 113.306258, 23.191448),
            ("长隆旅游度假区", 113.331839, 23.005809),
            ("萝岗香雪公园", 113.55, 23.1667),
            ("海心桥", 113.32, 23.11),
            ("南海神庙", 113.503936, 23.087264),
            ("陈家祠", 113.252777, 23.131612),
        };

        private TextBox txtIp = null!;
        private TextBox txtPort = null!;
        private NumericUpDown numInterval = null!;
        private Button btnStart = null!;
        private ListBox lstLog = null!;
        private Label lblStatus = null!;

        private System.Threading.Timer? _timer;
        private UdpClient? _udpClient;
        private volatile bool _running;
        private int _sendCount;

        // 缓存的公共数据
        private string _aqiLevel = "";
        private string _uvLevel = "";
        private string _uvIndex = "";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public MainForm()
        {
            InitUI();
        }

        private void InitUI()
        {
            this.Text = "广州景点天气UDP推送";
            this.Size = new System.Drawing.Size(820, 520);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            int y = 12;

            var lblInfo = new Label
            {
                Text = "6个广州景点实时天气 | UDP纯文本推送 | 数据源：广州气象局",
                Left = 12, Top = y, Width = 790, Height = 18,
                ForeColor = System.Drawing.Color.FromArgb(100, 100, 100)
            };
            y += 24;

            var lblIp = new Label { Text = "目标IP:", Left = 12, Top = y + 4, Width = 55, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
            txtIp = new TextBox { Left = 72, Top = y, Width = 140, Text = "127.0.0.1" };
            var lblPort = new Label { Text = "端口:", Left = 222, Top = y + 4, Width = 40 };
            txtPort = new TextBox { Left = 266, Top = y, Width = 70, Text = "9999" };
            var lblInt = new Label { Text = "间隔(分):", Left = 350, Top = y + 4, Width = 60 };
            numInterval = new NumericUpDown { Left = 414, Top = y, Width = 55, Minimum = 1, Maximum = 120, Value = 10 };
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

            // UDP格式说明
            var lblFormat = new Label
            {
                Text = "UDP格式: 景点名,温度,最低温,最高温,体感温度,湿度,天气,降雨量,空气质量,紫外线指数,紫外线等级",
                Left = 12, Top = y, Width = 790, Height = 18,
                ForeColor = System.Drawing.Color.FromArgb(80, 130, 80)
            };
            y += 22;

            lstLog = new ListBox
            {
                Left = 12, Top = y, Width = 780, Height = 260,
                Font = new System.Drawing.Font("Consolas", 9f)
            };

            this.Controls.AddRange(new Control[]
            {
                lblInfo, lblIp, txtIp, lblPort, txtPort, lblInt, numInterval,
                btnStart, btnOnce, btnClear, lblStatus, lblFormat, lstLog
            });
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
            btnStart.Text = "停止";
            lblStatus.Text = "状态：运行中";
            lblStatus.ForeColor = System.Drawing.Color.Green;
            _udpClient = new UdpClient();
            int intervalMin = (int)numInterval.Value;
            _timer = new System.Threading.Timer(_ => FetchAndSend(), null, 0, intervalMin * 60 * 1000);
        }

        private void Stop()
        {
            _running = false;
            _timer?.Dispose(); _timer = null;
            _udpClient?.Close(); _udpClient = null;
            btnStart.Text = "启动";
            lblStatus.Text = "状态：已停止";
            lblStatus.ForeColor = System.Drawing.Color.Gray;
        }

        private void FetchAndSend()
        {
            bool singleShot = !_running;
            if (singleShot) _udpClient = new UdpClient();

            try
            {
                string ip = this.Invoke(() => txtIp.Text.Trim());
                int port = int.Parse(this.Invoke(() => txtPort.Text.Trim()));
                var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);

                // 1. 先获取公共数据：空气质量和紫外线
                FetchPublicData();

                int ok = 0, fail = 0;

                for (int i = 0; i < Spots.Length; i++)
                {
                    var (name, lon, lat) = Spots[i];
                    try
                    {
                        var w = FetchSpotWeather(name, lon, lat);
                        // UDP纯文本：景点名,温度,最低温,最高温,体感温度,湿度,天气,降雨量,空气质量,紫外线指数,紫外线等级
                        string msg = $"{w.Name},{w.Temp:F1},{w.MinT:F0},{w.MaxT:F0},{w.Feels:F1},{w.Rh:F0},{w.Desc},{w.Rain:F1},{w.Aqi},{w.UvIndex},{w.UvLevel}";
                        byte[] bytes = Encoding.UTF8.GetBytes(msg);
                        _udpClient!.Send(bytes, bytes.Length, endpoint);
                        _sendCount++;
                        ok++;
                        Log($"  {name}: {w.Temp:F1}°C {w.Desc} 湿度{w.Rh:F0}% 体感{w.Feels:F1}°C AQI:{w.Aqi} UV:{w.UvIndex}({w.UvLevel})");
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Log($"  ✗ {name}: {ex.Message}");
                    }
                }

                Log($"✓ {ok}/{Spots.Length}景点推送完成, 累计{_sendCount}条 → {ip}:{port}");
            }
            catch (Exception ex)
            {
                Log($"✗ 错误: {ex.Message}");
            }
            finally
            {
                if (singleShot) { _udpClient?.Close(); _udpClient = null; }
            }
        }

        /// <summary>
        /// 获取公共数据：空气质量和紫外线指数（全广州统一）
        /// </summary>
        private void FetchPublicData()
        {
            try
            {
                // 空气质量
                string aqiJs = _http.GetStringAsync("http://www.tqyb.com.cn/data/gzWeather/gz_aqi.js").GetAwaiter().GetResult();
                string aqiJson = ExtractJson(aqiJs);
                using var aqiDoc = JsonDocument.Parse(aqiJson);
                var aqiRoot = aqiDoc.RootElement;
                _aqiLevel = aqiRoot.TryGetProperty("aqi_level", out var al) ? al.GetString()?.Trim() ?? "" : "";
            }
            catch { _aqiLevel = ""; }

            try
            {
                // 紫外线 + 生活指数
                string livingJs = _http.GetStringAsync("http://www.tqyb.com.cn/data/gzWeather/livingIndex.js").GetAwaiter().GetResult();
                string livingJson = ExtractJson(livingJs);
                using var livingDoc = JsonDocument.Parse(livingJson);
                var livingRoot = livingDoc.RootElement;
                if (livingRoot.TryGetProperty("ultraviolet", out var uv))
                {
                    _uvIndex = uv.TryGetProperty("index", out var idx) ? idx.GetString()?.Trim() ?? "" : "";
                    _uvLevel = uv.TryGetProperty("level", out var lv) ? lv.GetString()?.Trim() ?? "" : "";
                }
            }
            catch { _uvIndex = ""; _uvLevel = ""; }
        }

        private SpotData FetchSpotWeather(string name, double lon, double lat)
        {
            int lonKey = (int)(Math.Floor(lon / 0.05) * 0.05 * 100);
            int latKey = (int)(Math.Floor(lat / 0.05) * 0.05 * 100);

            string url = $"http://www.tqyb.com.cn/data/giftDailyCache/giftDaily{lonKey}_{latKey}.js";
            string js = _http.GetStringAsync(url).GetAwaiter().GetResult();
            string json = ExtractJson(js);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double[] giftT = ReadArr(root, "gift_t");
            double[] tigT = ReadArr(root, "tig_t");
            double[] rh2m = ReadArr(root, "gift_rh2m");
            double[] rain = ReadArr(root, "gift_rain");
            double[] maxt = ReadArr(root, "maxt");
            double[] mint = ReadArr(root, "mint");
            string[] descF = ReadStrArr(root, "desc_f");

            // 当前小时索引
            int hour = DateTime.Now.Hour;
            int hourIdx = (hour - 20 + 24) % 24;
            int dayBlock = hour >= 20 ? 1 : 0;
            int idx = dayBlock * 24 + hourIdx;

            double temp = Val(giftT, idx);
            double feels = Val(tigT, idx);
            double rh = Val(rh2m, idx);
            double rainVal = Val(rain, idx);

            int todayI = hour >= 20 ? 2 : 1;
            double maxT = Val(maxt, todayI);
            double minT = Val(mint, todayI);
            if (temp > -900 && maxT > -900) maxT = Math.Max(maxT, temp);
            if (temp > -900 && minT > -900) minT = Math.Min(minT, temp);

            string desc = descF.Length > 1 ? descF[1] : (descF.Length > 0 ? descF[0] : "未知");

            return new SpotData
            {
                Name = name,
                Temp = temp,
                MinT = minT,
                MaxT = maxT,
                Feels = feels,
                Rh = rh,
                Desc = desc,
                Rain = rainVal,
                Aqi = _aqiLevel,
                UvIndex = _uvIndex,
                UvLevel = _uvLevel
            };
        }

        /// <summary>
        /// 从JS中提取JSON对象，用括号计数器精确定位
        /// </summary>
        private static string ExtractJson(string js)
        {
            int eqIdx = js.IndexOf("= {");
            if (eqIdx < 0) eqIdx = js.IndexOf("={");
            if (eqIdx < 0) throw new Exception("JS格式异常，找不到= {");

            int jsonStart = js.IndexOf('{', eqIdx);
            if (jsonStart < 0) throw new Exception("找不到JSON起始{");

            int depth = 0;
            int jsonEnd = -1;
            for (int k = jsonStart; k < js.Length; k++)
            {
                if (js[k] == '{') depth++;
                else if (js[k] == '}') depth--;
                if (depth == 0)
                {
                    jsonEnd = k;
                    break;
                }
            }
            if (jsonEnd < 0) throw new Exception("找不到JSON结束}");

            return js.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        private static double[] ReadArr(JsonElement root, string prop)
        {
            if (!root.TryGetProperty(prop, out var e) || e.ValueKind != JsonValueKind.Array) return Array.Empty<double>();
            var a = new double[e.GetArrayLength()];
            for (int i = 0; i < a.Length; i++) a[i] = e[i].GetDouble();
            return a;
        }

        private static string[] ReadStrArr(JsonElement root, string prop)
        {
            if (!root.TryGetProperty(prop, out var e) || e.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var a = new string[e.GetArrayLength()];
            for (int i = 0; i < a.Length; i++) a[i] = e[i].GetString() ?? "";
            return a;
        }

        private static double Val(double[] a, int i)
        {
            if (a == null || i < 0 || i >= a.Length) return -999.9;
            double v = a[i];
            return v < -900 ? -999.9 : v;
        }

        private void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            if (lstLog.InvokeRequired)
                lstLog.Invoke(new Action(() => { lstLog.Items.Insert(0, line); if (lstLog.Items.Count > 500) lstLog.Items.RemoveAt(lstLog.Items.Count - 1); }));
            else
            { lstLog.Items.Insert(0, line); if (lstLog.Items.Count > 500) lstLog.Items.RemoveAt(lstLog.Items.Count - 1); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { Stop(); base.OnFormClosing(e); }
    }

    public class SpotData
    {
        public string Name = "";
        public double Temp = -999.9;
        public double MinT = -999.9;
        public double MaxT = -999.9;
        public double Feels = -999.9;
        public double Rh = -999.9;
        public string Desc = "";
        public double Rain = -999.9;
        public string Aqi = "";
        public string UvIndex = "";
        public string UvLevel = "";
    }

    static class Program
    {
        [STAThread]
        static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
    }
}
