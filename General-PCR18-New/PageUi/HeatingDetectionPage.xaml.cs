using General_PCR18.Common;
using General_PCR18.UControl;
using General_PCR18.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.DataVisualization.Charting;
using System.Windows.Input;

namespace General_PCR18.PageUi
{
    /// <summary>
    /// Interaction logic for HeatingDetectionPage.xaml
    /// </summary>
    public partial class HeatingDetectionPage : BasePage
    {
        #region 变量区域
        private readonly SampleUC[] sampleList = new SampleUC[18];
        private readonly HashSet<SampleUC> selectList = new HashSet<SampleUC>();

        private readonly SynchronizationContext context;

        private readonly Dictionary<int, List<double>> dataH1Temp = new Dictionary<int, List<double>>();  // 保存所有的H1温度
        private readonly Dictionary<int, List<double>> dataH3Temp = new Dictionary<int, List<double>>();  // 保存所有的H3温度

        private readonly int maxAxisXValue = 300;  // X轴最大点
        private readonly int maxAxisYValue = 120;  // Y轴最大点
        private readonly double[] xAxisIncValue = new double[18];  // X轴数据
        private HashSet<string> selectCurvesType = new HashSet<string>() { "H1", "H3" };  // 选中的类型
        private readonly Dictionary<int, BlockingCollection<double[]>> dataQueue = new Dictionary<int, BlockingCollection<double[]>>();

        private readonly System.Threading.Thread[] chartThreads = new System.Threading.Thread[18];

        private bool suppressH1ToggleEvent = false;
        private bool suppressH3ToggleEvent = false;

        private const int DefaultH1TempDeciC = 950; // 95.0℃
        private const int H3Temp61DeciC = 610;      // 61.0℃
        private const int H3Temp59DeciC = 590;      // 59.0℃
        private const int DefaultHeatTimeSec = 15;  // 默认加热时间（秒）

        // H3 温度切换时需要下发的命令（由用户提供）
        // 选择 61C：5E06 00 0E 1A 2C 00 00 00 00 00 00 00 B8
        private const string CmdH3Select61C = "5E06000E1A2C00000000000000B8";
        // 选择 59C：5E06 00 0E 19 64 00 00 00 00 00 00 00 EF
        private const string CmdH3Select59C = "5E06000E196400000000000000EF";

        #endregion

        public HeatingDetectionPage()
        {
            InitializeComponent();

            for (int i = 0; i < 18; i++)
            {
                dataQueue.Add(i, new BlockingCollection<double[]>());
            }

            // 初始化样本
            SampleData sampleData = new SampleData()
            {
                Width = 80,
                Height = 100,
                SeparateHeight = 5,
                Margin = 5,
                Sample_ClickEventTick = Sample_ClickEventTick,
                Sample_StartClickEventHandler = Sample_StartClickEventHandler,
            };
            InitSample(sampleGrid, sampleList, sampleData);

            InitChart();

            SampleEditActivate(false);

            context = SynchronizationContext.Current; // 获取当前 UI 线程的上下文

            // 订阅事件
            EventBus.OnHeatingDectionMessageReceived += EventBus_OnMessageReceived;

            // 初始化图表线程
            InitThread();

            // 测试
            //TestData();

            this.Loaded += Page_Loaded;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("===>HeatingDetectionPage Loaded");

            RefreshSampleUC(sampleList, true);

            // 进入页面默认全选 All，且给已插管孔位写入默认参数，用户无需再点选即可直接操作
            SelectAllAndApplyDefaults();
        }

        /// <summary>
        /// 处理全局事件
        /// </summary>
        /// <param name="obj"></param>
        private void EventBus_OnMessageReceived(NotificationMessage obj)
        {
            switch (obj.Code)
            {
                case MessageCode.PcrKeyStatus:
                    {
                        //LogHelper.Debug((object)("HeatingDetection收到消息: " + obj.Code));

                        context.Post(_ =>
                        {
                            RefreshSampleUC(sampleList, true);
                            // 插管后若已设置默认参数，则刷新状态使 Start 可用
                            if (selectList.Count > 0)
                            {
                                CheckHeatInputStatus(selectList);
                            }
                        }, null);
                    }
                    break;
                case MessageCode.TempUpdate:
                    {
                        LogHelper.Debug((object)("HeatingDetection收到消息: " + obj.Code));

                        //Key 试管序号，  Value = [H1, H2, H3]
                        Dictionary<int, double[]> dataArr = obj.Data;
                        foreach (var data in dataArr)
                        {
                            dataQueue[data.Key].Add(data.Value);

                            // H1 开关关闭（HeatH1Temp=0）时不触发光扫描启动
                            int h1Temp = GlobalData.DS.HeatH1Temp[data.Key];
                            if (h1Temp > 0 && data.Value[0] >= h1Temp)
                            {
                                // h1 等待 + h2加热时间后 h3 开始加热                                
                                EventBus.MainMsg(new MainNotificationMessage()
                                {
                                    Code = MainMessageCode.LightStart,
                                    TubeIndex = data.Key
                                });
                            }
                            // H3结束，停止光扫描
                            int h3Temp = GlobalData.DS.HeatH3Temp[data.Key];
                            if (h3Temp > 0 && data.Value[2] >= h3Temp)
                            {
                                EventBus.MainMsg(new MainNotificationMessage()
                                {
                                    Code = MainMessageCode.LightStop,
                                    TubeIndex = data.Key
                                });
                            }
                        }
                    }
                    break;
                case MessageCode.RefreshUI:
                    {
                        //LogHelper.Debug((object)("HeatingDetection收到消息: " + obj.Code));

                        // 刷新按钮状态
                        {
                            context.Post(_ =>
                            {
                                RefreshSampleUC(sampleList, true);
                                if (selectList.Count > 0)
                                {
                                    CheckHeatInputStatus(selectList);
                                }
                            }, null);
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// 编辑框状态切换
        /// </summary>
        /// <param name="activate"></param>
        private void SampleEditActivate(bool activate)
        {
            if (activate)
            {
                tglH1Enable.IsEnabled = true;
                tglH3TempMode.IsEnabled = true;
            }
            else
            {
                tglH1Enable.IsEnabled = false;
                tglH3TempMode.IsEnabled = false;
            }
        }

        private void EnsureDefaultTimes(int tubeIndex)
        {
            if (GlobalData.DS.HeatH1Time[tubeIndex] <= 0) GlobalData.DS.HeatH1Time[tubeIndex] = DefaultHeatTimeSec;
            if (GlobalData.DS.HeatH3Time[tubeIndex] <= 0) GlobalData.DS.HeatH3Time[tubeIndex] = DefaultHeatTimeSec;
        }

        /// <summary>
        /// 加热检测页：参数完备判定（不再依赖患者ID/检验日期/化验类型输入）
        /// </summary>
        private void CheckHeatInputStatus(HashSet<SampleUC> selects)
        {
            foreach (var s in selects)
            {
                bool tubeIsInto = GlobalData.DS.PCRKeyStatus[s.Index];

                int h1Temp = GlobalData.DS.HeatH1Temp[s.Index];
                int h3Temp = GlobalData.DS.HeatH3Temp[s.Index];
                int h1Time = GlobalData.DS.HeatH1Time[s.Index];
                int h3Time = GlobalData.DS.HeatH3Time[s.Index];

                if (tubeIsInto)
                {
                    if (h1Temp > 0 && h3Temp > 0 && h1Time > 0 && h3Time > 0
                        && GlobalData.GetStatus(s.Index) == TUBE_STATUS.NoParameters)
                    {
                        GlobalData.SetStatus(s.Index, TUBE_STATUS.ParametersSet);
                    }
                }

                RefreshButun(s);
            }
        }

        private void SelectAllAndApplyDefaults()
        {
            SampleEditActivate(true);

            selectList.Clear();
            AddSampleToSelected(sampleList, selectList);
            ChangeSelectBg(selectList, sampleList, true);

            // 默认：H1 开、H3=61℃
            suppressH1ToggleEvent = true;
            tglH1Enable.IsChecked = true;
            suppressH1ToggleEvent = false;

            suppressH3ToggleEvent = true;
            // IsChecked=True 表示选择 59C；默认选择 61C（左侧）
            tglH3TempMode.IsChecked = false;
            suppressH3ToggleEvent = false;

            ApplyH1ToggleToSelection(true);
            ApplyH3ToggleToSelection(false);
        }

        /// <summary>
        /// 初始化图表曲线
        /// </summary>
        private void InitChart()
        {
            ChartArea chartArea = new ChartArea("CharArea1");

            // X轴
            chartArea.AxisX.Title = "Time";
            //chartArea.AxisX.MajorGrid.Interval = 0.016666;
            //chartArea.AxisY.LabelStyle.Interval = 40;
            chartArea.AxisX.Interval = maxAxisXValue / 7;
            chartArea.AxisX.Minimum = 0;
            chartArea.AxisX.Maximum = maxAxisXValue;
            chartArea.AxisX.MajorGrid.LineColor = Tools.HexToColor("#eaecef"); // 设置 X 轴网格线颜色

            // 手动添加自定义标签
            chartArea.AxisX.CustomLabels.Add(new CustomLabel(0, 20, Tools.SecondsToHms(0), 0, LabelMarkStyle.None));
            chartArea.AxisX.CustomLabels.Add(new CustomLabel(60, 85, Tools.SecondsToHms(85), 0, LabelMarkStyle.None));
            chartArea.AxisX.CustomLabels.Add(new CustomLabel(150, 171, Tools.SecondsToHms(171), 0, LabelMarkStyle.None));
            chartArea.AxisX.CustomLabels.Add(new CustomLabel(240, 257, Tools.SecondsToHms(257), 0, LabelMarkStyle.None));


            // Y轴
            chartArea.AxisY.Title = "Temp";
            chartArea.AxisY.MajorGrid.Interval = 10;
            chartArea.AxisY.LabelStyle.Interval = 20;
            chartArea.AxisY.Minimum = 0;
            chartArea.AxisY.Maximum = maxAxisYValue;
            chartArea.AxisY.MajorGrid.LineColor = Tools.HexToColor("#eaecef"); // 设置 Y 轴网格线颜色

            // 启用 X 轴和 Y 轴的网格线
            chartArea.AxisX.MajorGrid.Enabled = true;
            chartArea.AxisY.MajorGrid.Enabled = true;

            chartArea.AxisX.Enabled = AxisEnabled.True;  // 始终显示
            chartArea.AxisY.Enabled = AxisEnabled.True;

            // 设置图表的背景颜色
            chartArea.BackColor = Tools.HexToColor("#fafafa");

            // 加入图表
            chartStandard.ChartAreas.Add(chartArea);

            // 样本
            for (int i = 0; i < 18; i++)
            {
                // H1
                Series seriesH1 = new Series("H1-" + i.ToString());
                seriesH1.ChartType = SeriesChartType.Line;
                seriesH1.Color = Tools.HexToColor("#4054b2");
                seriesH1.BorderWidth = 2;
                chartStandard.Series.Add(seriesH1);

                // H3
                Series seriesH3 = new Series("H3-" + i.ToString());
                seriesH3.ChartType = SeriesChartType.Line;
                seriesH3.Color = Tools.HexToColor("#9b2fae");
                seriesH3.BorderWidth = 2;
                chartStandard.Series.Add(seriesH3);
            }

            // 默认的，不显示
            Series seriesDefault = new Series("Default");
            seriesDefault.ChartType = SeriesChartType.Line;
            seriesDefault.Color = Tools.HexToColor("#f1f1f1");
            chartStandard.Series.Add(seriesDefault);
            seriesDefault.Points.AddXY(1, 0);

        }

        /// <summary>
        /// 初始化图表线程
        /// </summary>
        private void InitThread()
        {
            for (int i = 0; i < chartThreads.Length; i++)
            {
                int localIndex = i;
                chartThreads[localIndex] = new System.Threading.Thread(() => UpdateCurves(localIndex));

                chartThreads[localIndex].Start();
            }
        }

        /// <summary>
        /// 显示隐藏曲线
        /// </summary>
        /// <param name="index"></param>
        private void ShowSeries(int index)
        {
            foreach (var item in chartStandard.Series)
            {
                string[] arr = item.Name.Split('-');
                if (item.Name == "Default" || (int.Parse(arr[1]) == index && selectCurvesType.Contains(arr[0])))
                {
                    item.Enabled = true;
                }
                else
                {
                    item.Enabled = false;
                }
            }
        }

        private readonly object lockObj = new object();

        private void UpdateSeries(string name, List<double> X, List<double> Y, double dataX, double dataY)
        {

#if DEBUG
            LogHelper.Debug("更新{0}温度：{1}", name, string.Join(",", Y));
#endif

            context.Post(_ =>
            {
                X.Add(dataX);
                Y.Add(dataY);

                if (dataY > chartStandard.ChartAreas[0].AxisY.Maximum)
                {
                    double max = Math.Max(1, (int)Math.Round(dataY * 1.2));

                    chartStandard.ChartAreas[0].AxisY.Maximum = max;
                    chartStandard.ChartAreas[0].AxisY.MajorGrid.Interval = 20;
                    chartStandard.ChartAreas[0].AxisY.LabelStyle.Interval = 40;
                }

                chartStandard.Series[name].Points.Clear();
                chartStandard.Series[name].Points.DataBindXY(X, Y);
            }, null);
        }

        /// <summary>
        /// 更新曲线
        /// </summary>
        /// <param name="index">试管序号</param>
        private void UpdateCurves(int index)
        {
            LogHelper.Debug($"Heating Thread {Thread.CurrentThread.ManagedThreadId} is processing index: {index}");

            foreach (var data in dataQueue[index].GetConsumingEnumerable())
            {
                lock (lockObj)
                {
                    // H1
                    {
                        var name = "H1-" + index.ToString();
                        List<double> x = GlobalData.DataH1X[index];
                        List<double> y = GlobalData.DataH1Y[index];
                        UpdateSeries(name, x, y, xAxisIncValue[index], data[0]);
                    }

                    // H3
                    {
                        var name = "H3-" + index.ToString();
                        List<double> x = GlobalData.DataH3X[index];
                        List<double> y = GlobalData.DataH3Y[index];
                        UpdateSeries(name, x, y, xAxisIncValue[index], data[2]);
                    }

                    xAxisIncValue[index] += 2;
                }
            }
        }

        /// <summary>
        /// 点击样本
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="click"></param>
        private void Sample_ClickEventTick(SampleUC sender, bool click)
        {
            SampleEditActivate(true);

            if (Keyboard.Modifiers != ModifierKeys.Control)
            {
                selectList.Clear();

                // 管号
                if (string.IsNullOrEmpty(GlobalData.DS.HeatDockUnit[sender.Index]))
                {
                    int x = sender.Index % 6;
                    int y = sender.Index / 6;
                    string dockUnit = SampleAxisCharList[y + 6] + SampleAxisCharList[x];
                    GlobalData.DS.HeatDockUnit[sender.Index] = dockUnit;
                }

                // 回填开关状态（按当前孔位的参数）
                suppressH1ToggleEvent = true;
                tglH1Enable.IsChecked = GlobalData.DS.HeatH1Temp[sender.Index] > 0;
                suppressH1ToggleEvent = false;

                suppressH3ToggleEvent = true;
                // IsChecked=True 表示选择 59C
                tglH3TempMode.IsChecked = GlobalData.DS.HeatH3Temp[sender.Index] == H3Temp59DeciC;
                suppressH3ToggleEvent = false;

                selectList.Add(sender);

                context.Post(_ => ShowSeries(sender.Index), null);
            }
            else
            {
                // 多选
                var sam = selectList.Where(s => s.Index == sender.Index).FirstOrDefault();
                if (sam != null)
                {
                    selectList.Remove(sam);
                }
                else
                {
                    selectList.Add(sender);
                }
            }

            ChangeSelectBg(selectList, sampleList, true);
        }

        /// <summary>
        /// 点击样本下方按钮
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="click"></param>
        private void Sample_StartClickEventHandler(SampleUC sender, bool click)
        {
            StartButtonClick(sender);
        }

        /// <summary>
        /// 点击X轴
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void XTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            int lastCount = selectList.Count;
            SampleEditActivate(true);

            selectList.Clear();
            string text = (sender as Label).Content.ToString();
            AddSampleAxis(text, selectList, sampleList, lastCount);

            ChangeSelectBg(selectList, sampleList, true);
        }

        /// <summary>
        /// 右上角曲线类型按钮
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FAM_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var ctr = sender as Border;
            string tag = ctr.Tag.ToString();
            string bg = ctr.Background.ToString();
            if (bg == "#FFF1F4F9")
            {
                // 选中
                selectCurvesType.Remove(tag);
                ctr.Background = Tools.HexToBrush("#FF06919D");
                (ctr.Child as Label).Foreground = Tools.HexToBrush("#FFBFEDF0");
            }
            else
            {
                // 取消
                selectCurvesType.Add(tag);
                ctr.Background = Tools.HexToBrush("#FFF1F4F9");
                (ctr.Child as Label).Foreground = Tools.HexToBrush("#FF607C6D");
            }

            if (selectList.Count == 1)
            {
                ShowSeries(selectList.First().Index);
            }

        }

        private int GetDefaultH1TempDeciC()
        {
            return DefaultH1TempDeciC;
        }

        private void ApplyH1ToggleToSelection(bool enabled)
        {
            if (suppressH1ToggleEvent) { return; }
            if (selectList.Count == 0) { return; }

            foreach (var s in selectList)
            {
                EnsureDefaultTimes(s.Index);
                if (enabled)
                {
                    GlobalData.DS.HeatH1Temp[s.Index] = GetDefaultH1TempDeciC();
                }
                else
                {
                    GlobalData.DS.HeatH1Temp[s.Index] = 0;
                }
            }

            CheckHeatInputStatus(selectList);
        }

        private void tglH1Enable_Checked(object sender, RoutedEventArgs e)
        {
            ApplyH1ToggleToSelection(true);
        }

        private void tglH1Enable_Unchecked(object sender, RoutedEventArgs e)
        {
            ApplyH1ToggleToSelection(false);
        }

        // IsChecked=True 表示选择 59C
        private void ApplyH3ToggleToSelection(bool use59C)
        {
            if (suppressH3ToggleEvent) { return; }
            if (selectList.Count == 0) { return; }

            int temp = use59C ? H3Temp59DeciC : H3Temp61DeciC;
            foreach (var s in selectList)
            {
                EnsureDefaultTimes(s.Index);
                GlobalData.DS.HeatH3Temp[s.Index] = temp;
            }

            CheckHeatInputStatus(selectList);
        }

        private void tglH3TempMode_Checked(object sender, RoutedEventArgs e)
        {
            ApplyH3ToggleToSelection(true);
            // IsChecked=True => 选择 59C（右侧）
            SendH3TempModeCommand(is59C: true);
        }

        private void tglH3TempMode_Unchecked(object sender, RoutedEventArgs e)
        {
            ApplyH3ToggleToSelection(false);
            // IsChecked=False => 选择 61C（左侧）
            SendH3TempModeCommand(is59C: false);
        }

        private void SendH3TempModeCommand(bool is59C)
        {
            // 仅用户操作时下发；回填/初始化会走 suppressH3ToggleEvent
            if (suppressH3ToggleEvent) { return; }

            try
            {
                var mainWin = GlobalData.MainWin;
                if (mainWin == null) { return; }

                string cmd = is59C ? CmdH3Select59C : CmdH3Select61C;
                // 明确打日志，方便在输出窗口确认“确实触发了下发”
                LogHelper.Debug("H3温度切换 -> {0}，下发命令: {1}", is59C ? "59C" : "61C", StringUtils.FormatHex(cmd));
                Console.WriteLine($"[HeatingDetection] H3 toggle -> {(is59C ? "59C" : "61C")} send: {cmd}");

                mainWin.SendTestCmd(cmd);
            }
            catch (Exception ex)
            {
                try
                {
                    LogHelper.Error(ex);
                    Console.WriteLine($"[HeatingDetection] H3 toggle send failed: {ex.Message}");
                }
                catch { }
            }
        }


        private void TestData()
        {
            // 样本1
            var timer1 = new System.Timers.Timer(2000)
            {
                AutoReset = true,
                Enabled = true
            };
            timer1.Elapsed += (object sender, ElapsedEventArgs e) =>
            {
                Random r = new Random((int)DateTime.Now.Ticks);
                double v = r.NextDouble() * 120;
                dataQueue[0].Add(new double[] { v, 0, r.NextDouble() * 170 });
            };

            // 样本2
            var timer2 = new System.Timers.Timer(2000)
            {
                AutoReset = true,
                Enabled = true
            };
            timer2.Elapsed += (object sender, ElapsedEventArgs e) =>
            {
                Random r = new Random((int)DateTime.Now.Ticks * 10000);
                double v = r.NextDouble() * 120;
                dataQueue[1].Add(new double[] { v, 0, r.NextDouble() * 200 });
            };
        }
    }
}
