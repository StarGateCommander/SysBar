// SysBarSetup - instalator / deinstalator SysBar. SysBar.exe jest wbudowany jako zasob.
// Instaluje dla biezacego uzytkownika (bez uprawnien administratora).
//   SysBarSetup.exe              - instalacja z pytaniami
//   SysBarSetup.exe /silent      - instalacja bez pytan (dodaj /autostart, zeby wlaczyc start z Windows)
//   SysBarSetup.exe /uninstall   - odinstalowanie (rowniez z "Aplikacje" w Ustawieniach Windows)
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("SysBar - instalator")]
[assembly: AssemblyProduct("SysBar")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace SysBarSetup
{
    static class Program
    {
        const string AppName = "SysBar";
        const string Version = "1.0.1";
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SysBar";
        const string SettingsPath = @"Software\SysBar";

        static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\SysBar"); }
        }

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SysBar.lnk"); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool silent = HasArg(args, "/silent");
            try
            {
                if (HasArg(args, "/uninstall")) return Uninstall(silent);
                return Install(silent, HasArg(args, "/autostart"));
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show("Operacja nie powiodła się:\n\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static bool HasArg(string[] args, string name)
        {
            foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static int Install(bool silent, bool autostart)
        {
            if (!silent)
            {
                DialogResult r = MessageBox.Show(
                    "Zainstalować SysBar " + Version + "?\n\nFolder: " + InstallDir,
                    AppName + " - instalator", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return 2;
                autostart = MessageBox.Show("Uruchamiać SysBar automatycznie przy starcie Windows?",
                    AppName + " - instalator", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            }

            StopRunning();
            Directory.CreateDirectory(InstallDir);
            string exe = Path.Combine(InstallDir, "SysBar.exe");
            string setup = Path.Combine(InstallDir, "SysBarSetup.exe");

            using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream("SysBar.exe"))
            using (FileStream dst = new FileStream(exe, FileMode.Create, FileAccess.Write))
                src.CopyTo(dst);

            // Kopia instalatora zostaje w folderze programu jako deinstalator.
            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(setup), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, setup, true);

            CreateShortcut(ShortcutPath, exe);

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallPath))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", AppName);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + setup + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + setup + "\" /uninstall /silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)((new FileInfo(exe).Length + new FileInfo(setup).Length) / 1024), RegistryValueKind.DWord);
            }

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunPath))
            {
                if (autostart) k.SetValue(AppName, "\"" + exe + "\"");
                else k.DeleteValue(AppName, false);
            }

            Process.Start(exe);
            if (!silent)
                MessageBox.Show("SysBar został zainstalowany i uruchomiony.\n\nPrawy przycisk myszy na pasku otwiera menu.",
                    AppName + " - instalator", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static int Uninstall(bool silent)
        {
            if (!silent && MessageBox.Show("Odinstalować SysBar?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 2;

            StopRunning();
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath, true))
                if (k != null) k.DeleteValue(AppName, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(SettingsPath, false);
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);

            string exe = Path.Combine(InstallDir, "SysBar.exe");
            if (File.Exists(exe)) File.Delete(exe);

            if (!silent) MessageBox.Show("SysBar został odinstalowany.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

            // Deinstalator nie moze skasowac sam siebie, dopoki dziala - robi to cmd chwile po jego zakonczeniu.
            string setup = Path.Combine(InstallDir, "SysBarSetup.exe");
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & del /q \"" + setup + "\" & rmdir \"" + InstallDir + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi);
            return 0;
        }

        static void StopRunning()
        {
            foreach (Process p in Process.GetProcessesByName("SysBar"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
            Thread.Sleep(300);
        }

        static void CreateShortcut(string lnk, string target)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Pasek systemowy: CPU, RAM, sieć, GPU, zegar" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
    }
}
