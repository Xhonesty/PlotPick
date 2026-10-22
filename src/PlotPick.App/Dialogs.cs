using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlotPick.Core;
namespace PlotPick.App;
public static class Dialogs
{
    public static string[]? Form(Window owner,string title,params (string Label,string Value)[] fields)
    {
        var panel=new StackPanel{Margin=new Thickness(22)};var boxes=new List<TextBox>();
        foreach(var field in fields){panel.Children.Add(new TextBlock{Text=field.Label,Margin=new Thickness(3,8,3,2),TextWrapping=TextWrapping.Wrap});var box=new TextBox{Text=field.Value,MinWidth=350};panel.Children.Add(box);boxes.Add(box);}
        var win=new Window{Owner=owner,Title=title,SizeToContent=SizeToContent.WidthAndHeight,MaxHeight=800,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};
        var cancel=new Button{Content="取消",IsCancel=true};var ok=new Button{Content="确定",IsDefault=true,Style=(Style)Application.Current.FindResource("Primary")};ok.Click+=(s,e)=>win.DialogResult=true;buttons.Children.Add(cancel);buttons.Children.Add(ok);panel.Children.Add(buttons);
        win.Loaded+=(s,e)=>{if(boxes.Count>0){boxes[0].Focus();boxes[0].SelectAll();}};
        return win.ShowDialog()==true?boxes.Select(b=>b.Text).ToArray():null;
    }
    public static void Text(Window owner,string title,string text)
    {
        var box=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(0),Margin=new Thickness(18)};
        new Window{Owner=owner,Title=title,Width=720,Height=540,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=box}.ShowDialog();
    }
    public static bool Confirm(Window owner,string message)=>MessageBox.Show(owner,message,"图拾 PlotPick",MessageBoxButton.OKCancel,MessageBoxImage.Information)==MessageBoxResult.OK;
    public static (int Page,int Dpi,string? Password)? ChoosePdf(Window owner,byte[] bytes)
    {
        string? password=null;int count;
        try{count=Pdf.PageCount(bytes);}catch(InvalidDataException ex)when(ex.Message.Contains("密码")){var f=Form(owner,"PDF 密码",("仅用于本次打开，不保存密码",""));if(f is null)return null;password=f[0];count=Pdf.PageCount(bytes,password);}
        var grid=new Grid{Margin=new Thickness(16)};grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(175)});grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left=new StackPanel();var page=new TextBox{Text="1"};left.Children.Add(new TextBlock{Text=$"共 {count} 页 · 输入页码"});left.Children.Add(page);
        var dpi=new ComboBox{ItemsSource=new[]{300,600},SelectedIndex=0};left.Children.Add(new TextBlock{Text="取数分辨率 DPI",Margin=new Thickness(4,12,0,0)});left.Children.Add(dpi);
        var preview=new Image{Stretch=Stretch.Uniform,Margin=new Thickness(16,0,0,0)};Grid.SetColumn(preview,1);grid.Children.Add(left);grid.Children.Add(preview);
        var win=new Window{Owner=owner,Title="选择 PDF 页面",Width=830,Height=680,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=grid};
        var refresh=new Button{Content="预览页面"};var import=new Button{Content="导入此页",Style=(Style)Application.Current.FindResource("Primary")};left.Children.Add(refresh);left.Children.Add(import);
        void Refresh(){if(int.TryParse(page.Text,out var n)&&n>=1&&n<=count)preview.Source=Bitmap(Pdf.Render(bytes,n-1,72,password));}
        refresh.Click+=(s,e)=>Refresh();import.Click+=(s,e)=>{if(int.TryParse(page.Text,out var n)&&n>=1&&n<=count)win.DialogResult=true;else MessageBox.Show("请输入有效页码。");};win.Loaded+=(s,e)=>Refresh();
        return win.ShowDialog()==true?(int.Parse(page.Text), (int)dpi.SelectedItem,password):null;
    }
    public static BitmapSource Bitmap(byte[] bytes){var bmp=new BitmapImage();bmp.BeginInit();bmp.CacheOption=BitmapCacheOption.OnLoad;bmp.StreamSource=new MemoryStream(bytes);bmp.EndInit();bmp.Freeze();return bmp;}
}
