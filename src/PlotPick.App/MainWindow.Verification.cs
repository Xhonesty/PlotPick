using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Imaging=PlotPick.Core.Imaging;
using PlotPick.Core;

namespace PlotPick.App;
public partial class MainWindow
{
    // Isolated engineering harness: fixture-only window, no user file or native mouse injection.
    public void VerifyEditing(string directory)
    {
        Directory.CreateDirectory(directory);autosave.Stop();var checks=new List<string>();
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);checks.Add(label);}
        var root=(FrameworkElement)Content;root.Measure(new Size(1540,960));root.Arrange(new Rect(0,0,1540,960));root.UpdateLayout();
        DemoClick(this,new());series!.Points=[new(){Pixel=new(300,300)},new(){Pixel=new(400,300)},new(){Pixel=new(500,300)}];Refresh(image:false);history.Clear();
        var view=new CanvasView(2.3,-170,-130);Canvas.RestoreView(view);Canvas.Tool="insert";
        PixelClicked(new(350,300));Check(series.Points.Count==4&&series.Points[1].Pixel==new Vec(350,300),"automatic insertion preserves path order");
        Check(Canvas.View==view,"insertion preserves zoom/pan");UndoClick(this,new());Check(series!.Points.Count==3&&Canvas.View==view,"undo preserves zoom/pan");RedoClick(this,new());Check(series!.Points.Count==4&&Canvas.View==view,"redo preserves zoom/pan");
        Canvas.SelectRegion(new(290,290,70,20),false);Check(Canvas.Selected.Count==2&&DataTable.SelectedItems.Count==2,"box selection synchronizes table");
        Canvas.SelectRegion(new(490,290,20,20),true);Check(Canvas.Selected.Count==3,"control box selection appends");
        using(var inputSource=new HwndSource(new HwndSourceParameters("PlotPick key routing verification"){Width=1,Height=1,WindowStyle=unchecked((int)0x80000000)}))
        {
            SeriesName.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,inputSource,0,Key.Delete){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            Check(series.Points.Count==4,"Delete in text editor does not delete selected data");
            Canvas.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,inputSource,0,Key.Delete){RoutedEvent=Keyboard.PreviewKeyDownEvent});
        }
        Check(series.Points.Count==1&&Canvas.Selected.Count==0,"Delete routed from canvas deletes selected points only");UndoClick(this,new());Check(series!.Points.Count==4&&Canvas.View==view,"batch deletion undo restores points and view");
        Canvas.Selected=[series.Points[1].Id];InsertMode.SelectedIndex=1;PixelClicked(new(330,300));PixelClicked(new(340,300));Check(series.Points[1].Pixel.X==330&&series.Points[2].Pixel.X==340&&series.Points[3].Pixel.X==350,"manual before mode preserves successive click order");
        series.Locked=true;int count=series.Points.Count;Canvas.SelectRegion(new(0,0,1400,900),false);DeleteClick(this,new());Check(series.Points.Count==count,"locked series deletion blocked");series.Locked=false;CancelInteraction();Check(Canvas.Selected.Count==0,"escape clears selection");
        var firstId=plot!.Id;Change();var doc=project.Documents[0];var other=AddPlot(doc,Imaging.Demo(),"second view");Refresh(other.Id);var secondView=new CanvasView(.7,40,60);Canvas.RestoreView(secondView);Refresh(firstId);Check(Canvas.View==view,"switching plots restores first viewport");Refresh(other.Id);Check(Canvas.View==secondView,"each plot retains its own viewport");
        Change();doc.Plots.Remove(plot!);Refresh();UndoClick(this,new());Refresh(other.Id);Check(Canvas.View==secondView,"undo plot removal restores cached viewport");
        Refresh(firstId);var imageBefore=plot!.ImageAsset;Change();plot.ImageAsset=project.AddAsset(Imaging.Process(project.Assets[plot.ImageAsset],"旋转"),".png");Refresh();UndoClick(this,new());Check(plot!.ImageAsset==imageBefore&&Canvas.View==view,"undo image replacement reloads bitmap without fitting");
        CalibrateClick(this,new());Check(CalibrationPrompt.Text.Contains("1/4：X 轴起点"),"calibration first prompt");PixelClicked(new(150,750));Check(CalibrationPrompt.Text.Contains("2/4：X 轴终点"),"calibration second prompt");PixelClicked(new(1250,750));Check(CalibrationPrompt.Text.Contains("3/4：Y 轴起点"),"calibration third prompt");PixelClicked(new(150,750));Check(CalibrationPrompt.Text.Contains("4/4：Y 轴终点"),"calibration fourth prompt");PixelClicked(new(150,100));Check(CalibrationBanner.Visibility==Visibility.Collapsed,"calibration completion clears banner");
        YAxisSide.SelectedIndex=1;CalibrateClick(this,new());PixelClicked(new(150,750));PixelClicked(new(1250,750));Check(CalibrationPrompt.Text.Contains("右 Y 轴起点"),"right Y calibration is explicit");CancelInteraction();
        var color=Canvas.ColorAt(new(300,300));Canvas.ShowMask=true;Canvas.Tool="color";PixelClicked(new(300,300));Check(series!.Color==$"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}","color click samples original pixels");
        Check(!Canvas.ContainsPixel(new(-1,0))&&!Canvas.ContainsPixel(new(1400,900)),"outside image cannot sample");
        var path=Path.Combine(directory,"edited-roundtrip.plotpick");ProjectStore.Save(project,path);var reopened=ProjectStore.Load(path);Check(Json.Serialize(project)==Json.Serialize(reopened),"edited points survive project round trip");
        dirty=false;File.WriteAllText(Path.Combine(directory,"editing-checks.json"),Json.Serialize(new{Passed=true,Checks=checks,Method="WPF fixture window, routed operation handlers and canvas state; no physical input"}));
    }
}
