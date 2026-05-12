using System.Windows;

namespace ProtocolTestBench
{
    public partial class MainWindow
    {
        // 顶部工具按钮：打开独立 JSON 编辑/校验/美化弹窗。
        private void JsonFormatterButton_Click(object sender, RoutedEventArgs e)
        {
            JsonFormatterWindow window = new JsonFormatterWindow
            {
                Owner = this
            };

            try
            {
                // JSON 美化器作为辅助弹窗使用，模态打开可以避免主窗口和编辑器状态互相干扰。
                IsEnabled = true;
                window.Show();
            }
            finally
            {
                IsEnabled = true;
                Activate();
            }
        }
    }
}
