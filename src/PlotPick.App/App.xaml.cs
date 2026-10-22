using System.IO;
using System.Windows;
namespace PlotPick.App;
public partial class App:Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if(e.Args.Length>=2&&e.Args[0] is "--render-ui" or "--verify-editing")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(()=>{try{var window=new MainWindow();if(e.Args[0]=="--verify-editing")window.VerifyEditing(e.Args[1]);else window.RenderSnapshots(e.Args[1]);Shutdown(0);}catch(Exception ex){File.WriteAllText(Path.Combine(e.Args[1],"render-error.txt"),ex.ToString());Shutdown(1);}});
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException+=(s,args)=>{MessageBox.Show(args.Exception.Message,"图拾 · 操作未完成",MessageBoxButton.OK,MessageBoxImage.Warning);args.Handled=true;};
        AppDomain.CurrentDomain.UnhandledException+=(s,args)=>{try{Directory.CreateDirectory(StateDirectory);File.AppendAllText(Path.Combine(StateDirectory,"crash.log"),DateTime.UtcNow+" "+((Exception)args.ExceptionObject).GetType().Name+Environment.NewLine);}catch{}};
        new MainWindow().Show();
    }
    public static string StateDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PlotPick");
}

