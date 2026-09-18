using System;
using System.IO;
using System.Windows.Forms;

namespace LineScriptTerminal
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            try
            {
                var ex = new LocalExecutor();
                if (args.Length > 0 && Directory.Exists(args[0]))
                    ex.Execute("-cd- " + args[0] + " -/-");
                Application.Run(new MainForm(ex));
            }
            catch (Exception ex)
            {
                string log = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LineScriptTerminal", "crash.log");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                    File.WriteAllText(log,
                        DateTime.Now + "\r\n" + ex + "\r\n\r\n",
                        System.Text.Encoding.UTF8);
                }
                catch { }
                MessageBox.Show(
                    ex.Message + "\r\n\r\n" + ex.StackTrace + "\r\n\r\nЛог: " + log,
                    "LineScript Terminal — ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
