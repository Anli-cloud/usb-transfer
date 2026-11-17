using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace UsbTransfer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool firstInstance;
            using (var instance = new Mutex(true, @"Local\UsbTransfer.Win.Singleton", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("快捷传输已在运行。请从 Windows 通知区域打开。", "USB 快捷传输");
                    return;
                }

                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbQuickTransfer");
                    string destination = DefaultDestination(data);
                    var controller = new TransferController(app, data, destination);
                    controller.Start();
                    app.Run();
                }
                catch (Exception error)
                {
                    MessageBox.Show("工具启动失败。\n" + error.GetBaseException().Message, "USB 快捷传输", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static string DefaultDestination(string data)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documents) && !documents.StartsWith(@"\\", StringComparison.Ordinal))
            {
                string root = Path.GetPathRoot(documents);
                if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType != DriveType.Network)
                    return Path.Combine(documents, "测试素材");
            }
            return Path.Combine(data, "测试素材");
        }
    }
}
