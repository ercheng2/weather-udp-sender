using System;
using System.Collections.Generic;
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
    public class MainForm : Form
    {
        private TextBox txtUrl = null!;
        private TextBox txtIp = null!;
        private TextBox txtPort = null!;
        private TextBox txtInterval = null!;
        private Button btnStart = null!;
        private ListBox lstLog = null!;
        private Label lblStatus = null!;

        private System.Threading.Timer? _timer;
        private UdpClient? _udpClient;
        private volatile bool _running;
        private int _sendCount;

        // 拼音→中文映射（与tqyb数据源对应）
        private static readonly Dictionary<string, string> CityNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            {"beijing", "北京"}, {"shanghai", "上海"}, {"BCGZ", "广州"},
            {"hefei", "合肥"}, {"aomen", "澳门"}, {"fuzhou", "福州"},
            {"shamen", "厦门"}, {"lanzhou", "兰州"}, {"guilin", "桂林"},
            {"nanning", "南宁"}, {"beihai", "北海"}, {"guiyang", "贵阳"},
            {"haikou", "海口"}, {"sanya", "三亚"}, {"shijiazhuang", "石家庄"},
            {"zhengzhou", "郑州"}, {"haerbin", "哈尔滨"}, {"wuhan", "武汉"},
            {"zhangsha", "长沙"}, {"zhangchun", "长春"}, {"nanjing", "南京"},
            {"lianyungang", "连云港"}, {"nanchang", "南昌"}, {"ganzhou", "赣州"},
            {"dalian", "大连"}, {"shenyang", "沈阳"}, {"anshan", "鞍山"},
            {"huhehaote", "呼和浩特"}, {"yinchuan", "银川"}, {"xining", "西宁"},
            {"jinan", "济南"}, {"qingdao", "青岛"}, {"taiyuan", "太原"},
            {"xian", "西安"}, {"chengdou", "成都"}, {"taibei", "台北"},
            {"tianjin", "天津"}, {"lasa", "拉萨"}, {"wulumuqi", "乌鲁木齐"},
            {"kunming", "昆明"}, {"hangzhou", "杭州"}, {"ningbo", "宁波"},
            {"zhongqing", "重庆"},
        };

        public MainForm()
        {
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            // 窗体
            this.Text = "天气数据UDP推送工具";
            this.Size = new System.Drawing.Size(680, 520);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            int y = 12;
            int labelW = 90;

            // 数据源URL
            var lbl1 = new Label { Text = "数据源URL:", Left = 12, Top = y + 3, Width = labelW, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
            txtUrl = new TextBox
            {
                Left = 108, Top = y, Width = 540,
                Text = "http://www.tqyb.com.cn/data/gzWeather/domesticCityForecast.js"
            };
            y += 32;

            // UDP目标IP
            var lbl2 = new Label { Text = "目标IP:", Left = 12, Top = y + 3, Width = labelW, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
            txtIp = new TextBox { Left = 108, Top = y, Width = 200, Text = "127.0.0.1" };
            var lbl3 = new Label { Text = "端口:", Left = 320, Top = y + 3, Width = 40 };
            txtPort = new TextBox { Left = 364, Top = y, Width = 80, Text = "9999" };
            var lbl4 = new Label { Text = "间隔(分):", Left = 460, Top = y + 3, Width = 60 };
            txtInterval = new TextBox { Left = 524, Top = y, Width = 60, Text = "10" };
            y += 36;

            // 按钮
            btnStart = new Button { Text = "启动", Left = 108, Top = y, Width = 80, Height = 30 };
            btnStart.Click += BtnStart_Click;
            var btnOnce = new Button { Text = "立即执行一次", Left = 200, Top = y, Width = 120, Height = 30 };
            btnOnce.Click += (_, _) => FetchAndSend();
            var btnClear = new Button { Text = "清空日志", Left = 332, Top = y, Width = 80, Height = 30 };
            btnClear.Click += (_, _) => lstLog.Items.Clear();
            y += 40;

            // 状态栏
            lblStatus = new Label { Text = "状态：已停止", Left = 12, Top = y, Width = 300, ForeColor = System.Drawing.Color.Gray };
            y += 22;

            // 日志列表
            lstLog = new ListBox
            {
                Left = 12, Top = y, Width = 636, Height = 290,
                Font = new System.Drawing.Font("Consolas", 9f)
            };
            y += 294;

            this.Controls.AddRange(new Control[] {
                lbl1, txtUrl, lbl2, txtIp, lbl3, txtPort, lbl4, txtInterval,
                btnStart, btnOnce, btnClear, lblStatus, lstLog
            });
        }

        private void BtnStart_Click(object? sender, EventArgs e)
        {
            if (_running)
            {
                Stop();
            }
            else
            {
                Start();
            }
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
            lblStatus.ForeColor = System.Drawing.Color.Green;

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
            lblStatus.ForeColor = System.Drawing.Color.Gray;
        }

        private void FetchAndSend()
        {
            if (!_running && _udpClient == null)
            {
                // 单次执行模式
                _udpClient = new UdpClient();
            }

            try
            {
                var url = txtUrl.Text.Trim();
                Log($"正在获取: {url}");

                string jsContent;
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    jsContent = client.GetStringAsync(url).GetAwaiter().GetResult();
                }

                // 从JS中提取JSON: var gz_domesticCityForecast = {...}
                var jsonStart = jsContent.IndexOf('{');
                var jsonEnd = jsContent.LastIndexOf('}');
                if (jsonStart < 0 || jsonEnd < 0)
                {
                    Log("错误：无法从响应中提取JSON数据");
                    return;
                }
                var jsonStr = jsContent.Substring(jsonStart, jsonEnd - jsonStart + 1);

                // 解析JSON
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var dataElem))
                {
                    Log("错误：JSON中无data字段");
                    return;
                }

                var targetIp = txtIp.Text.Trim();
                var targetPort = int.Parse(txtPort.Text.Trim());
                var endPoint = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);

                int cityCount = 0;
                int skipCount = 0;

                foreach (var prop in dataElem.EnumerateObject())
                {
                    var pinyin = prop.Name;
                    var cityName = CityNameMap.TryGetValue(pinyin, out var cn) ? cn : pinyin;

                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var arr = prop.Value.EnumerateArray().ToList();
                        if (arr.Count == 0)
                        {
                            skipCount++;
                            continue;
                        }

                        // 发送3天预报，每天一条
                        for (int day = 0; day < arr.Count; day++)
                        {
                            var dayData = arr[day];
                            var mint = dayData.TryGetProperty("mint", out var m) ? m.GetDouble() : 0;
                            var maxt = dayData.TryGetProperty("maxt", out var x) ? x.GetDouble() : 0;
                            var cont = dayData.TryGetProperty("cont", out var c) ? c.GetString() ?? "" : "";

                            // 格式: 城市,最低温,最高温,天气,第几天(0=今天,1=明天,2=后天)
                            var msg = $"{cityName},{mint},{maxt},{cont},{day}";
                            var bytes = Encoding.UTF8.GetBytes(msg);
                            _udpClient!.Send(bytes, bytes.Length, endPoint);
                            _sendCount++;
                        }
                        cityCount++;
                    }
                }

                Log($"✓ 完成: {cityCount}个城市, {skipCount}个缺数据, 共{_sendCount}条UDP已发送 → {targetIp}:{targetPort}");
            }
            catch (Exception ex)
            {
                Log($"✗ 错误: {ex.Message}");
            }
            finally
            {
                // 单次执行模式用完关闭
                if (!_running)
                {
                    _udpClient?.Close();
                    _udpClient = null;
                }
            }
        }

        private void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            if (lstLog.InvokeRequired)
            {
                lstLog.Invoke(new Action(() => AddLogLine(line)));
            }
            else
            {
                AddLogLine(line);
            }
        }

        private void AddLogLine(string line)
        {
            lstLog.Items.Insert(0, line);
            if (lstLog.Items.Count > 200)
                lstLog.Items.RemoveAt(lstLog.Items.Count - 1);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Stop();
            base.OnFormClosing(e);
        }
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
