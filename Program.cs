using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace WeatherUdpSender
{
    /// <summary>
    /// 广州景点实时天气 UDP推送工具
    /// 数据源：广州气象局 giftDailyCache 接口
    /// </summary>
    public class MainForm : Form
    {
        // ===== 6个景点配置 =====
        private static readonly ScenicSpot[] Spots = new[]
        {
            new ScenicSpot("白云山风景名胜区", 113.306258, 23.191448),
            new ScenicSpot("长隆旅游度假区", 113.331839, 23.005809),
            new ScenicSpot("萝岗香雪公园", 113.55, 23.1667),
            new ScenicSpot("海心桥", 113.32, 23.11),
            new ScenicSpot("南海神庙", 113.503936, 23.087264),
            new ScenicSpot("陈家祠", 113.252777, 23.131612),
        };

        private TextBox txtIp = null!;
        private TextBox txtPort = null!;
        private TextBox txtInterval = null!;
        private Button btnStart = null!;
        private ListBox lstLog = null!;
        private Label lblStatus = null!;
        private PictureBox picPreview = null!;

        private System.Threading.Timer? _timer;
        private UdpClient? _udpClient;
        private volatile bool _running;
        private int _sendCount;
        private SpotWeather[] _lastWeather = Array.Empty<SpotWeather>();

        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        public MainForm()
        {
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            this.Text = "广州景点天气UDP推送";
            this.Size = new Size(920, 680);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            int y = 12;

            // 标题说明
            var lblTitle = new Label
            {
                Text = "数据源：广州气象局 giftDailyCache 接口（6个景点实时天气）",
                Left = 12, Top = y, Width = 880, Height = 20,
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            y += 26;

            // UDP目标IP
            var lbl2 = new Label { Text = "目标IP:", Left = 12, Top = y + 3, Width = 55, TextAlign = ContentAlignment.MiddleRight };
            txtIp = new TextBox { Left = 72, Top = y, Width = 140, Text = "127.0.0.1" };
            var lbl3 = new Label { Text = "端口:", Left = 222, Top = y + 3, Width = 40 };
            txtPort = new TextBox { Left = 266, Top = y, Width = 70, Text = "9999" };
            var lbl4 = new Label { Text = "间隔(分):", Left = 350, Top = y + 3, Width = 60 };
            txtInterval = new TextBox { Left = 414, Top = y, Width = 50, Text = "10" };

            // 截图保存路径
            var lbl5 = new Label { Text = "截图目录:", Left = 480, Top = y + 3, Width = 60 };
            var txtScreenshotDir = new TextBox { Left = 544, Top = y, Width = 250, Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots") };

            y += 36;

            // 按钮
            btnStart = new Button { Text = "启动", Left = 12, Top = y, Width = 80, Height = 30 };
            btnStart.Click += BtnStart_Click;
            var btnOnce = new Button { Text = "立即获取", Left = 100, Top = y, Width = 100, Height = 30 };
            btnOnce.Click += (_, _) => FetchAndSend();
            var btnScreenshot = new Button { Text = "截图预览", Left = 208, Top = y, Width = 100, Height = 30 };
            btnScreenshot.Click += (_, _) => TakeScreenshot(txtScreenshotDir.Text.Trim());
            var btnClear = new Button { Text = "清空日志", Left = 316, Top = y, Width = 80, Height = 30 };
            btnClear.Click += (_, _) => lstLog.Items.Clear();
            y += 38;

            // 状态栏
            lblStatus = new Label { Text = "状态：已停止", Left = 12, Top = y, Width = 400, ForeColor = Color.Gray };
            y += 24;

            // 天气预览面板
            picPreview = new PictureBox
            {
                Left = 12, Top = y, Width = 880, Height = 260,
                BackColor = Color.FromArgb(15, 25, 60),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            y += 268;

            // 日志列表
            lstLog = new ListBox
            {
                Left = 12, Top = y, Width = 880, Height = 180,
                Font = new Font("Consolas", 9f)
            };

            this.Controls.AddRange(new Control[]
            {
                lblTitle,
                lbl2, txtIp, lbl3, txtPort, lbl4, txtInterval,
                lbl5, txtScreenshotDir,
                btnStart, btnOnce, btnScreenshot, btnClear,
                lblStatus, picPreview, lstLog
            });
        }

        private void BtnStart_Click(object? sender, EventArgs e)
        {
            if (_running) Stop();
            else Start();
        }

        private void Start()
        {
            if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("端口范围1-65535", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!int.TryParse(txtInterval.Text, out int intervalMin) || intervalMin < 1)
            {
                MessageBox.Show("间隔至少1分钟", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!IPAddress.TryParse(txtIp.Text, out _))
            {
                MessageBox.Show("IP地址格式错误", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _running = true;
            _sendCount = 0;
            btnStart.Text = "停止";
            lblStatus.Text = "状态：运行中";
            lblStatus.ForeColor = Color.Green;

            _udpClient = new UdpClient();
            _timer = new System.Threading.Timer(_ => FetchAndSend(), null, 0, intervalMin * 60 * 1000);
        }

        private void Stop()
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
            _udpClient?.Close();
            _udpClient = null;
            btnStart.Text = "启动";
            lblStatus.Text = "状态：已停止";
            lblStatus.ForeColor = Color.Gray;
        }

        private void FetchAndSend()
        {
            var singleShot = !_running;
            if (singleShot) _udpClient = new UdpClient();

            try
            {
                var targetIp = this.Invoke(() => txtIp.Text.Trim());
                var targetPort = int.Parse(this.Invoke(() => txtPort.Text.Trim()));
                var endPoint = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);

                var weathers = new SpotWeather[Spots.Length];

                for (int i = 0; i < Spots.Length; i++)
                {
                    try
                    {
                        weathers[i] = FetchSpotWeather(Spots[i]);
                    }
                    catch (Exception ex)
                    {
                        weathers[i] = new SpotWeather { Name = Spots[i].Name, Error = ex.Message };
                        Log($"  ✗ {Spots[i].Name}: {ex.Message}");
                    }
                }

                _lastWeather = weathers;

                // UDP发送
                int sent = 0;
                foreach (var w in weathers)
                {
                    if (w.Error != null) continue;
                    // 格式：景点名,当前温度,最低温,最高温,体感温度,湿度,天气描述
                    var msg = $"{w.Name},{w.CurrentTemp:F1},{w.MinTemp:F0},{w.MaxTemp:F0},{w.FeelsLike:F1},{w.Humidity:F0},{w.Description}";
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    _udpClient!.Send(bytes, bytes.Length, endPoint);
                    sent++;
                    _sendCount++;
                }

                // 更新预览面板
                this.Invoke(() => DrawWeatherPanel(weathers));

                Log($"✓ 完成: {sent}/{Spots.Length}个景点, 累计{_sendCount}条UDP → {targetIp}:{targetPort}");
            }
            catch (Exception ex)
            {
                Log($"✗ 错误: {ex.Message}");
            }
            finally
            {
                if (singleShot)
                {
                    _udpClient?.Close();
                    _udpClient = null;
                }
            }
        }

        private SpotWeather FetchSpotWeather(ScenicSpot spot)
        {
            // 计算giftDailyCache的网格坐标（0.05精度向下取整）
            double gridLon = Math.Floor(spot.Longitude / 0.05) * 0.05;
            double gridLat = Math.Floor(spot.Latitude / 0.05) * 0.05;
            int lonKey = (int)(gridLon * 100);
            int latKey = (int)(gridLat * 100);

            string url = $"http://www.tqyb.com.cn/data/giftDailyCache/giftDaily{lonKey}_{latKey}.js";

            string jsContent = _httpClient.GetStringAsync(url).GetAwaiter().GetResult();

            // 从JS中提取JSON
            int jsonStart = jsContent.IndexOf('{');
            int jsonEnd = jsContent.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0)
                throw new Exception("无法从响应中提取JSON");

            string jsonStr = jsContent.Substring(jsonStart, jsonEnd - jsonStart + 1);

            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            // 解析各字段
            var giftT = GetArray(root, "gift_t");
            var tigT = GetArray(root, "tig_t");
            var giftRh2m = GetArray(root, "gift_rh2m");
            var descF = GetArray<string>(root, "desc_f");
            var maxt = GetArray(root, "maxt");
            var mint = GetArray(root, "mint");

            // 计算当前小时在gift_t中的索引
            // gift_t从20:00开始，每小时一个值，每天24个值
            int currentHour = DateTime.Now.Hour;
            int hourIndex = (currentHour - 20 + 24) % 24;
            // 第一组24个值是"昨晚→今天"
            // 如果当前时间在20:00之前，数据在第一个24块中
            // 如果当前时间在20:00之后，数据在第二个24块中（今天晚上）
            int dayBlock = currentHour >= 20 ? 1 : 0;
            int globalIndex = dayBlock * 24 + hourIndex;

            double currentTemp = SafeGet(giftT, globalIndex);
            double feelsLike = SafeGet(tigT, globalIndex);
            double humidity = SafeGet(giftRh2m, globalIndex);
            string description = descF.Length > 1 ? descF[1] : (descF.Length > 0 ? descF[0] : "未知");

            // 今日最高/最低温 - maxt[1]和mint[1]是"今天"的
            int todayIdx = currentHour >= 20 ? 2 : 1;
            double maxTemp = SafeGet(maxt, todayIdx);
            double minTemp = SafeGet(mint, todayIdx);

            // 如果当前温度比预报最高温高或最低温低，用当前温度
            if (currentTemp > -900 && maxTemp > -900) maxTemp = Math.Max(maxTemp, currentTemp);
            if (currentTemp > -900 && minTemp > -900) minTemp = Math.Min(minTemp, currentTemp);

            return new SpotWeather
            {
                Name = spot.Name,
                CurrentTemp = currentTemp,
                MinTemp = minTemp,
                MaxTemp = maxTemp,
                FeelsLike = feelsLike,
                Humidity = humidity,
                Description = description
            };
        }

        private static double[] GetArray(JsonElement root, string propName)
        {
            if (!root.TryGetProperty(propName, out var elem) || elem.ValueKind != JsonValueKind.Array)
                return Array.Empty<double>();
            var result = new double[elem.GetArrayLength()];
            for (int i = 0; i < result.Length; i++)
                result[i] = elem[i].GetDouble();
            return result;
        }

        private static T[] GetArray<T>(JsonElement root, string propName) where T : class
        {
            if (!root.TryGetProperty(propName, out var elem) || elem.ValueKind != JsonValueKind.Array)
                return Array.Empty<T>();
            var result = new T[elem.GetArrayLength()];
            for (int i = 0; i < result.Length; i++)
            {
                if (typeof(T) == typeof(string))
                    result[i] = (T)(object)(elem[i].GetString() ?? "");
                else
                    result[i] = default!;
            }
            return result;
        }

        private static double SafeGet(double[] arr, int idx)
        {
            if (arr == null || idx < 0 || idx >= arr.Length) return -999.9;
            double v = arr[idx];
            return v < -900 ? -999.9 : v;
        }

        // ===== 绘制天气面板（深蓝渐变背景，6宫格） =====
        private void DrawWeatherPanel(SpotWeather[] weathers)
        {
            int w = picPreview.Width;
            int h = picPreview.Height;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                // 深蓝渐变背景
                using (var brush = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                    Color.FromArgb(10, 20, 55), Color.FromArgb(25, 45, 100), 90f))
                {
                    g.FillRectangle(brush, 0, 0, w, h);
                }

                // 标题
                string title = $"广州景点实时天气  {DateTime.Now:yyyy-MM-dd HH:mm}";
                using (var titleFont = new Font("微软雅黑", 14f, FontStyle.Bold))
                using (var titleBrush = new SolidBrush(Color.White))
                {
                    g.DrawString(title, titleFont, titleBrush, new PointF(20, 10));
                }

                // 6宫格 3列×2行
                int cols = 3, rows = 2;
                int padding = 10;
                int topOffset = 40;
                int cellW = (w - padding * (cols + 1)) / cols;
                int cellH = (h - topOffset - padding * (rows + 1)) / rows;

                for (int i = 0; i < weathers.Length; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    int x = padding + col * (cellW + padding);
                    int y2 = topOffset + padding + row * (cellH + padding);

                    DrawWeatherCell(g, x, y2, cellW, cellH, weathers[i]);
                }
            }

            picPreview.Image?.Dispose();
            picPreview.Image = bmp;
        }

        private void DrawWeatherCell(Graphics g, int x, int y, int w, int h, SpotWeather weather)
        {
            // 卡片背景（半透明深蓝）
            using (var cardBrush = new SolidBrush(Color.FromArgb(30, 50, 95)))
            using (var borderPen = new Pen(Color.FromArgb(60, 90, 160), 1))
            {
                g.FillRectangle(cardBrush, x, y, w, h);
                g.DrawRectangle(borderPen, x, y, w, h);
            }

            bool hasError = weather.Error != null;

            // 景点名
            using (var nameFont = new Font("微软雅黑", 11f, FontStyle.Bold))
            using (var nameBrush = new SolidBrush(Color.FromArgb(200, 220, 255)))
            {
                g.DrawString(weather.Name, nameFont, nameBrush, new PointF(x + 10, y + 6));
            }

            if (hasError)
            {
                using (var errFont = new Font("微软雅黑", 9f))
                using (var errBrush = new SolidBrush(Color.FromArgb(255, 120, 120)))
                {
                    g.DrawString($"获取失败: {weather.Error}", errFont, errBrush, new PointF(x + 10, y + 45));
                }
                return;
            }

            // 当前温度（大字）
            string tempStr = weather.CurrentTemp > -900 ? $"{weather.CurrentTemp:F0}°C" : "--°C";
            using (var tempFont = new Font("Arial", 28f, FontStyle.Bold))
            using (var tempBrush = new SolidBrush(Color.White))
            {
                g.DrawString(tempStr, tempFont, tempBrush, new PointF(x + 10, y + 30));
            }

            // 温度区间
            string rangeStr = "";
            if (weather.MinTemp > -900 && weather.MaxTemp > -900)
                rangeStr = $"{weather.MinTemp:F0}°~{weather.MaxTemp:F0}°C";
            using (var rangeFont = new Font("微软雅黑", 9f))
            using (var rangeBrush = new SolidBrush(Color.FromArgb(160, 190, 240)))
            {
                g.DrawString(rangeStr, rangeFont, rangeBrush, new PointF(x + 10, y + 70));
            }

            // 天气描述
            using (var descFont = new Font("微软雅黑", 10f))
            using (var descBrush = new SolidBrush(Color.FromArgb(255, 220, 100)))
            {
                g.DrawString(weather.Description, descFont, descBrush, new PointF(x + 120, y + 70));
            }

            // 详细信息（第二行）
            int detailY = y + 95;
            using (var detailFont = new Font("微软雅黑", 9f))
            using (var detailBrush = new SolidBrush(Color.FromArgb(180, 200, 230)))
            {
                if (weather.FeelsLike > -900)
                    g.DrawString($"体感 {weather.FeelsLike:F0}°C", detailFont, detailBrush, new PointF(x + 10, detailY));
                if (weather.Humidity > -900)
                    g.DrawString($"湿度 {weather.Humidity:F0}%", detailFont, detailBrush, new PointF(x + 120, detailY));
            }
        }

        // ===== 截图保存 =====
        private void TakeScreenshot(string dir)
        {
            try
            {
                if (_lastWeather.Length == 0)
                {
                    MessageBox.Show("请先获取天气数据", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // 生成高分辨率截图（960×540）
                int imgW = 960, imgH = 540;
                using (var bmp = new Bitmap(imgW, imgH, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                    // 深蓝渐变背景
                    using (var brush = new LinearGradientBrush(new Rectangle(0, 0, imgW, imgH),
                        Color.FromArgb(10, 20, 55), Color.FromArgb(25, 45, 100), 90f))
                    {
                        g.FillRectangle(brush, 0, 0, imgW, imgH);
                    }

                    // 标题
                    string title = $"广州景点实时天气  {DateTime.Now:yyyy-MM-dd HH:mm}";
                    using (var titleFont = new Font("微软雅黑", 18f, FontStyle.Bold))
                    using (var titleBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString(title, titleFont, titleBrush, new PointF(24, 12));
                    }

                    // 6宫格 3列×2行
                    int cols = 3, rows = 2;
                    int padding = 14;
                    int topOffset = 55;
                    int cellW = (imgW - padding * (cols + 1)) / cols;
                    int cellH = (imgH - topOffset - padding * (rows + 1)) / rows;

                    for (int i = 0; i < _lastWeather.Length; i++)
                    {
                        int col = i % cols;
                        int row = i / cols;
                        int cx = padding + col * (cellW + padding);
                        int cy = topOffset + padding + row * (cellH + padding);

                        DrawWeatherCellHighRes(g, cx, cy, cellW, cellH, _lastWeather[i]);
                    }
                }

                // 保存
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string filename = $"weather_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string filepath = Path.Combine(dir, filename);
                bmp.Save(filepath, ImageFormat.Png);

                Log($"截图已保存: {filepath}");
                MessageBox.Show($"截图已保存到:\n{filepath}", "截图完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log($"截图失败: {ex.Message}");
                MessageBox.Show($"截图失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DrawWeatherCellHighRes(Graphics g, int x, int y, int w, int h, SpotWeather weather)
        {
            // 卡片背景
            using (var cardBrush = new SolidBrush(Color.FromArgb(30, 50, 95)))
            using (var borderPen = new Pen(Color.FromArgb(60, 90, 160), 2))
            {
                g.FillRectangle(cardBrush, x, y, w, h);
                g.DrawRectangle(borderPen, x, y, w, h);
            }

            bool hasError = weather.Error != null;

            using (var nameFont = new Font("微软雅黑", 14f, FontStyle.Bold))
            using (var nameBrush = new SolidBrush(Color.FromArgb(200, 220, 255)))
            {
                g.DrawString(weather.Name, nameFont, nameBrush, new PointF(x + 14, y + 8));
            }

            if (hasError)
            {
                using (var errFont = new Font("微软雅黑", 11f))
                using (var errBrush = new SolidBrush(Color.FromArgb(255, 120, 120)))
                {
                    g.DrawString($"获取失败: {weather.Error}", errFont, errBrush, new PointF(x + 14, y + 55));
                }
                return;
            }

            // 当前温度（大字）
            string tempStr = weather.CurrentTemp > -900 ? $"{weather.CurrentTemp:F0}°C" : "--°C";
            using (var tempFont = new Font("Arial", 36f, FontStyle.Bold))
            using (var tempBrush = new SolidBrush(Color.White))
            {
                g.DrawString(tempStr, tempFont, tempBrush, new PointF(x + 14, y + 38));
            }

            // 温度区间
            string rangeStr = "";
            if (weather.MinTemp > -900 && weather.MaxTemp > -900)
                rangeStr = $"{weather.MinTemp:F0}°~{weather.MaxTemp:F0}°C";
            using (var rangeFont = new Font("微软雅黑", 11f))
            using (var rangeBrush = new SolidBrush(Color.FromArgb(160, 190, 240)))
            {
                g.DrawString(rangeStr, rangeFont, rangeBrush, new PointF(x + 14, y + 88));
            }

            // 天气描述
            using (var descFont = new Font("微软雅黑", 12f))
            using (var descBrush = new SolidBrush(Color.FromArgb(255, 220, 100)))
            {
                g.DrawString(weather.Description, descFont, descBrush, new PointF(x + 150, y + 87));
            }

            // 详细信息
            int detailY = y + 118;
            using (var detailFont = new Font("微软雅黑", 11f))
            using (var detailBrush = new SolidBrush(Color.FromArgb(180, 200, 230)))
            {
                if (weather.FeelsLike > -900)
                    g.DrawString($"体感 {weather.FeelsLike:F0}°C", detailFont, detailBrush, new PointF(x + 14, detailY));
                if (weather.Humidity > -900)
                    g.DrawString($"湿度 {weather.Humidity:F0}%", detailFont, detailBrush, new PointF(x + 150, detailY));
            }
        }

        private void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            if (lstLog.InvokeRequired)
                lstLog.Invoke(new Action(() => AddLogLine(line)));
            else
                AddLogLine(line);
        }

        private void AddLogLine(string line)
        {
            lstLog.Items.Insert(0, line);
            if (lstLog.Items.Count > 300)
                lstLog.Items.RemoveAt(lstLog.Items.Count - 1);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Stop();
            base.OnFormClosing(e);
        }
    }

    // ===== 数据结构 =====
    public record ScenicSpot(string Name, double Longitude, double Latitude);

    public class SpotWeather
    {
        public string Name = "";
        public double CurrentTemp = -999.9;
        public double MinTemp = -999.9;
        public double MaxTemp = -999.9;
        public double FeelsLike = -999.9;
        public double Humidity = -999.9;
        public string Description = "";
        public string? Error;
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }
}
