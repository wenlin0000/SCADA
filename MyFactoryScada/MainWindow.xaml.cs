using System.Windows;
using ScottPlot;
using ScottPlot.Plottables;
namespace MyFactoryScada // <--- 這裡必須與 x:Class 的前半段完全相同！
{
    public partial class MainWindow : Window
    {
        private DataLogger? _speedLogger;
        private DataLogger? _tempLogger;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            InitializeChart();

            // 訂閱 ViewModel 傳來的新數據點，即時推進曲線圖
            viewModel.OnTrendDataUpdated += (speed, temp) =>
            {
                _speedLogger?.Add(speed);
                _tempLogger?.Add(temp);
                TrendPlot.Refresh(); // 重新繪製畫面
            };
        }

        private void InitializeChart()
        {
            // 設定深色工業風圖表背景與格線
            TrendPlot.Plot.FigureBackground.Color = Color.FromHex("#1E232E");
            TrendPlot.Plot.DataBackground.Color = Color.FromHex("#12151C");
            TrendPlot.Plot.Axes.Color(Color.FromHex("#94A3B8"));
            TrendPlot.Plot.Grid.MajorLineColor = Color.FromHex("#283040");


            //////////////////////////////////////////////////////////////////////////////////////////////////////
            // 1. 全域設定 ScottPlot 預設字型為「微軟正黑體」，一次解決標題、X軸、Y軸與圖例的中文方塊問題！
            TrendPlot.Plot.Axes.Title.Label.FontName = "Microsoft JhengHei";
            TrendPlot.Plot.Axes.Bottom.Label.FontName = "Microsoft JhengHei";
            TrendPlot.Plot.Axes.Left.Label.FontName = "Microsoft JhengHei";

            TrendPlot.Plot.Font.Set("Microsoft JhengHei");

            // 2. 接著再設定你的圖表標題與座標軸標籤
            TrendPlot.Plot.Title("主軸轉速與溫度即時趨勢");
            TrendPlot.Plot.XLabel("採樣時間點");
            TrendPlot.Plot.YLabel("數值");

            //TrendPlot.Refresh();
            ////////////////////////////////////////////////////////////////////////////////////////////////////
            // 建立兩條即時滾動曲線 (DataLogger 內建自動向右滾動視窗功能)
            _speedLogger = TrendPlot.Plot.Add.DataLogger();
            _speedLogger.Color = Color.FromHex("#38BDF8"); // 亮藍色代表轉速
            _speedLogger.LineWidth = 2;
            _speedLogger.ViewSlide(100); // 畫面自動跟隨並顯示最新 100 個數據點

            _tempLogger = TrendPlot.Plot.Add.DataLogger();
            _tempLogger.Color = Color.FromHex("#FBBF24"); // 琥珀黃代表溫度
            _tempLogger.LineWidth = 2;
            _tempLogger.ViewSlide(100);

            TrendPlot.Plot.Title("主軸轉速與溫度即時監控趨勢");
        }
    }
}
