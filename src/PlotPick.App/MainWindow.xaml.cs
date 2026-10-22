using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PlotPick.Core;
using SkiaSharp;
using Series=PlotPick.Core.Series;

namespace PlotPick.App;
public partial class MainWindow:Window
{
    readonly MainState state=new();readonly History history=new();readonly DispatcherTimer autosave=new(){Interval=TimeSpan.FromSeconds(30)};
    Project project=new();Plot? plot;Series? series;string? projectPath;bool dirty,refreshing,tableSync,saving;CancellationTokenSource? task;
    int revision;readonly List<Vec> anchors=[];string? errorDirection;Guid? errorPoint;
    string RecoveryPath=>Path.Combine(App.StateDirectory,"recovery-"+project.Id+".plotpick");
    public MainWindow()
    {
        InitializeComponent();DataContext=state;state.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(state.Busy))UpdateNavigation();};
        PreviewKeyUp+=(s,e)=>{if(e.Key==Key.B){Canvas.OriginalOnly=false;Canvas.InvalidateVisual();}};
        Deactivated+=(s,e)=>{Canvas.OriginalOnly=false;Canvas.InvalidateVisual();};
        Canvas.FindInsertion=GetInsertion;Canvas.PixelClicked=PixelClicked;Canvas.RegionSelected=RegionSelected;Canvas.PointMoved=PointMoved;
        Canvas.SelectionUpdated=ids=>{if(tableSync)return;manualTail=null;SyncTableSelection();};
        Canvas.Hover=(p,z)=>{state.Zoom=$"{z*100:F0}%";try{state.Coordinates=plot?.Axes.Calibrated==true?FormatCoordinates(plot.Axes.ToData(p,series?.RightAxis??false)):$"像素 {p.X:F0}, {p.Y:F0}";}catch{state.Coordinates="坐标超出范围";}};
        autosave.Tick+=async(s,e)=>await AutoSave();autosave.Start();Loaded+=OnLoaded;
    }
    string FormatCoordinates(Vec p)=>$"X {p.X:G6}  Y {p.Y:G6}";
    public void RenderSnapshots(string directory)
    {
        Directory.CreateDirectory(directory);autosave.Stop();var root=(FrameworkElement)Content;
        void Snapshot(string name,int width,int height,double dpi)
        {
            root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();Canvas.Fit();root.UpdateLayout();
            var image=new RenderTargetBitmap((int)(width*dpi/96),(int)(height*dpi/96),dpi,dpi,System.Windows.Media.PixelFormats.Pbgra32);image.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(directory,name));encoder.Save(stream);
        }
        Snapshot("home.png",1440,900,96);DemoClick(this,new());
        ProjectStore.Save(project,Path.Combine(directory,"three-curves-empty.plotpick"));
        foreach(var item in plot!.Series)item.Points=Extraction.Run(project.Assets[plot.ImageAsset],item.Kind,SeriesOptions.Get(plot,item)).Points;
        Refresh(image:false);Snapshot("workspace.png",1540,960,96);Snapshot("workspace-150.png",1540,960,144);Snapshot("workspace-200.png",1100,720,192);
        Canvas.Tool="box-select";Canvas.SelectRegion(new(390,100,130,640),false);UpdateNavigation();Snapshot("editing.png",1540,960,96);
        Canvas.ClearSelection();Canvas.Tool="insert";Canvas.InspectionSample=series!.Points[70].Pixel;Canvas.SnapshotInspection=true;UpdateNavigation();Snapshot("insertion.png",1540,960,96);Snapshot("insertion-200.png",1100,720,192);
        CancelInteraction();CalibrateClick(this,new());Canvas.InspectionSample=new(150,750);Canvas.SnapshotInspection=true;Snapshot("calibration.png",1540,960,96);Snapshot("calibration-200.png",1100,720,192);
        CancelInteraction();Canvas.Tool="color";Canvas.InspectionSample=series!.Points[70].Pixel;Canvas.SnapshotInspection=true;UpdateNavigation();Snapshot("color-preview.png",1540,960,96);Snapshot("color-preview-200.png",1100,720,192);
        Canvas.SnapshotInspection=false;Canvas.Tool="select";UpdateNavigation();
        ProjectStore.Save(project,Path.Combine(directory,"three-curves.plotpick"));OpenExport();Snapshot("export.png",1540,960,96);Snapshot("export-200.png",1100,720,192);
        File.WriteAllText(Path.Combine(directory,"render-result.json"),Json.Serialize(new{Home=true,Workspace=true,WindowIconLoaded=Icon is not null,Points=series!.Points.Count,Dpi=new[]{100,150,200},Method="WPF offscreen RenderTargetBitmap; not desktop capture"}));dirty=false;
    }
    async void OnLoaded(object sender,RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.StateDirectory);
        var argument=Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a=>File.Exists(a));
        if(argument is not null){await OpenPath(argument);return;}
        var recovery=Directory.GetFiles(App.StateDirectory,"recovery-*.plotpick").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if(recovery is not null&&Dialogs.Confirm(this,"发现上次未关闭项目的恢复快照，是否恢复？")){project=ProjectStore.Load(recovery);dirty=true;Refresh();}
    }
    bool Ready(bool calibrated=false)
    {
        if(state.Busy){state.Status="当前任务尚未完成，可取消后再操作。";return false;}
        if(plot is null||series is null){state.Status="请先导入图表。";return false;}
        if(calibrated&&!plot.Axes.Calibrated){state.Status="请先完成坐标标定。";Inspector.SelectedIndex=0;return false;}return true;
    }
    void Change(){history.Push(project);dirty=true;revision++;state.SaveStatus="有未保存的更改";}
    void MarkDerivedStale()
    {
        if(plot is null||series is null)return;var changed=new HashSet<Guid>{series.Id};bool added;
        do{added=false;foreach(var child in plot.Series.Where(s=>s.ParentSeries.HasValue&&changed.Contains(s.ParentSeries.Value)))if(changed.Add(child.Id)){child.Stale=true;added=true;}}while(added);
    }
    void Fail(Exception ex){state.Status="未完成："+ex.Message;UpdateNavigation();}
    void Run(Action action){try{action();}catch(Exception ex){Fail(ex);}}
    async Task Work(Func<CancellationToken,Task> action)
    {
        if(state.Busy)return;task=new();state.Busy=true;state.Progress=0;
        try{await action(task.Token);}catch(OperationCanceledException){state.Status="任务已取消，原数据保留。";}catch(Exception ex){Fail(ex);}finally{state.Busy=false;state.Progress=0;task.Dispose();task=null;}
    }
    void Refresh(Guid? plotId=null,Guid? seriesId=null,bool image=true)
    {
        RememberView();SeriesOptions.Initialize(project);CloseExport();refreshing=true;var pid=plotId??plot?.Id;var sid=seriesId??series?.Id;
        state.ProjectName=project.Name;state.Plots.Clear();foreach(var d in project.Documents)foreach(var p in d.Plots)state.Plots.Add(new(p.Id,p.Name,$"{d.Name} · 第 {p.Page} 页"));
        plot=project.Plots.FirstOrDefault(p=>p.Id==pid)??project.Plots.FirstOrDefault();series=plot?.Series.FirstOrDefault(s=>s.Id==sid)??plot?.Series.FirstOrDefault();
        PlotList.SelectedItem=state.Plots.FirstOrDefault(p=>p.Id==plot?.Id);state.Series.Clear();if(plot is not null)foreach(var s in plot.Series)state.Series.Add(s);SeriesList.SelectedItem=series;TableSeriesPicker.SelectedItem=series;
        Canvas.Plot=plot;Canvas.CurrentSeries=series;Canvas.Selected.IntersectWith(series?.Points.Select(p=>p.Id)??[]);manualTail=null;Home.Visibility=plot is null?Visibility.Visible:Visibility.Collapsed;
        RestorePlotView(image);if(plot is not null)LoadInspector();
        RefreshRows();Canvas.InvalidateVisual();refreshing=false;SyncPreviews();
    }
    void LoadInspector()
    {
        if(plot is null||series is null)return;var d=project.Documents.First(x=>x.Plots.Contains(plot));var x=plot.Axes.X;var y=YAxisSide.SelectedIndex==1?plot.Axes.RightY??new Axis():plot.Axes.Y;
        XScale.SelectedIndex=(int)x.Scale;YScale.SelectedIndex=(int)y.Scale;DateFormat.Text=x.Scale==AxisScale.Date?x.DateFormat:y.DateFormat;
        XMin.Text=x.Format(x.Min);XMax.Text=x.Format(x.Max);YMin.Text=y.Format(y.Min);YMax.Text=y.Format(y.Max);XLabel.Text=x.Label;YLabel.Text=y.Label;
        CalibrationInfo.Text=plot.Axes.Calibrated?"✓ 已标定 · 标定点可通过重新标定调整":"尚未标定 · 请先指定四个刻度位置";
        SeriesName.Text=series.Name;SeriesType.SelectedIndex=Math.Min((int)series.Kind,3);Color.Text=series.Color;RightAxis.IsChecked=series.RightAxis;SeriesVisible.IsChecked=series.Visible;SeriesLocked.IsChecked=series.Locked;Marker.SelectedIndex=series.Marker;Dashed.IsChecked=series.Dashed;
        var options=SeriesOptions.Get(plot,series);SharedRegion.IsChecked=series.UseSharedRegion;Tolerance.Value=options.Tolerance;SampleStep.Text=options.Step.ToString();DataStep.Text=options.DataStep?.ToString(CultureInfo.InvariantCulture)??"";MaxGap.Text=options.MaxGap.ToString();MinArea.Text=options.MinArea.ToString();MaxArea.Text=options.MaxArea.ToString();GridSuppression.IsChecked=options.SuppressGrid;Intensity.IsChecked=options.IntensityMode;
        PlotName.Text=plot.Name;PaperTitle.Text=d.Title;Doi.Text=d.Doi;Figure.Text=plot.Figure;Notes.Text=plot.Notes;ChartTitle.Text=plot.ChartTitle;SourceInfo.Text=$"{d.Name}\n第 {plot.Page} 页 · {plot.Width} × {plot.Height} px\n{plot.Dpi} DPI\n{plot.Processing}";AnalysisReport.Text=series.Processing;
    }
    void RefreshRows()
    {
        tableSync=true;state.Rows.Clear();if(plot is not null&&series is not null)
        {
            int i=0;foreach(var p in series.Points){string x="—",y="—";try{if(plot.Axes.Calibrated){var value=series.ValueOf(p,plot.Axes);x=plot.Axes.X.Format(value.X);y=plot.Axes.GetY(series.RightAxis).Format(value.Y);}}catch{}
                state.Rows.Add(new(){Id=p.Id,Index=++i,X=x,Y=y,Category=p.Category,Origin=p.Origin switch{PointOrigin.Manual=>"手动",PointOrigin.Automatic=>"自动",PointOrigin.Corrected=>"修正",_=>"派生"},Review=p.Review==ReviewState.Accepted?"已复核":"待复核"});}
        }
        state.PointCount=$"{state.Rows.Count:N0} 个点";tableSync=false;SyncTableSelection();SeriesList.Items.Refresh();UpdateNavigation();
    }
    void PlotChanged(object sender,SelectionChangedEventArgs e){if(refreshing||state.Busy)return;if(PlotList.SelectedItem is PlotItem item){if(!PersistSettings()){refreshing=true;PlotList.SelectedItem=state.Plots.FirstOrDefault(p=>p.Id==plot?.Id);refreshing=false;return;}ClearPreview();Refresh(item.Id);}}
    void SeriesChanged(object sender,SelectionChangedEventArgs e){if(refreshing||state.Busy)return;if(SeriesList.SelectedItem is Series s){if(!PersistSettings()){refreshing=true;SeriesList.SelectedItem=series;refreshing=false;return;}Refresh(seriesId:s.Id,image:false);}}
    async void OpenClick(object sender,RoutedEventArgs e)
    {
        if(state.Busy)return;var dialog=new OpenFileDialog{Filter="图像、PDF 或项目|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.pdf;*.plotpick",Multiselect=true};
        if(dialog.ShowDialog()==true)foreach(var path in dialog.FileNames)await OpenPath(path);
    }
    async Task OpenPath(string path)
    {
        try
        {
            if(Path.GetExtension(path).Equals(".plotpick",StringComparison.OrdinalIgnoreCase))
            {
                if(!CanReplace())return;var loaded=ProjectStore.Load(path);DeleteRecovery();project=loaded;projectPath=path;dirty=false;history.Clear();ClearPreview();Refresh();state.SaveStatus="已保存 · "+Path.GetFileName(path);return;
            }
            var bytes=await File.ReadAllBytesAsync(path);string ext=Path.GetExtension(path).ToLowerInvariant();
            if(ext==".pdf")
            {
                var choice=Dialogs.ChoosePdf(this,bytes);if(choice is null)return;var ch=choice.Value;
                await Work(async token=>{state.Status="正在渲染 PDF 页面…";var png=await Task.Run(()=>Pdf.Render(bytes,ch.Page-1,ch.Dpi,ch.Password),token);token.ThrowIfCancellationRequested();Change();var d=new Document{Name=Path.GetFileName(path),SourceAsset=project.AddAsset(bytes,".pdf")};project.Documents.Add(d);var p=AddPlot(d,png,$"图表 {d.Plots.Count+1}",ch.Page,ch.Dpi);Refresh(p.Id);state.Status="PDF 已导入，可框选图表区域后开始标定。";});
            }
            else if(ext is ".tif" or ".tiff")
            {
                var decoder=BitmapDecoder.Create(new MemoryStream(bytes),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);Change();var d=new Document{Name=Path.GetFileName(path),SourceAsset=project.AddAsset(bytes,ext)};project.Documents.Add(d);
                for(int i=0;i<decoder.Frames.Count;i++){var encoder=new PngBitmapEncoder();encoder.Frames.Add(decoder.Frames[i]);using var stream=new MemoryStream();encoder.Save(stream);AddPlot(d,stream.ToArray(),$"图表 {i+1}",i+1);}
                Refresh(d.Plots[0].Id);
            }
            else {using var image=SKBitmap.Decode(bytes)??throw new InvalidDataException("不支持或已损坏的图片。");if((long)image.Width*image.Height>100_000_000)throw new InvalidDataException("图片超过 1 亿像素，请先缩小。");Change();var d=new Document{Name=Path.GetFileName(path),SourceAsset=project.AddAsset(bytes,ext)};project.Documents.Add(d);var p=AddPlot(d,Imaging.Png(image),Path.GetFileNameWithoutExtension(path));Refresh(p.Id);state.Status="图片已导入 · 下一步：四点标定";}
            if(project.Name=="未命名项目"){project.Name=Path.GetFileNameWithoutExtension(path);state.ProjectName=project.Name;}
        }catch(Exception ex){Fail(ex);}
    }
    Plot AddPlot(Document document,byte[] image,string name,int page=1,int dpi=300)
    {
        using var bitmap=SKBitmap.Decode(image)??throw new InvalidDataException("无法解码图片。");var asset=project.AddAsset(image,".png");var p=new Plot{Name=name,ImageAsset=asset,OriginalAsset=asset,Width=bitmap.Width,Height=bitmap.Height,Page=page,Dpi=dpi};document.Plots.Add(p);return p;
    }
    async void MorePdfClick(object s,RoutedEventArgs e)
    {
        if(!Ready())return;var d=project.Documents.First(d=>d.Plots.Contains(plot!));if(!d.SourceAsset.EndsWith(".pdf")){state.Status="当前文档不是 PDF。";return;}var bytes=project.Assets[d.SourceAsset];
        Run(()=>{var choice=Dialogs.ChoosePdf(this,bytes);if(choice is null)return;var c=choice.Value;var image=Pdf.Render(bytes,c.Page-1,c.Dpi,c.Password);Change();var p=AddPlot(d,image,$"第 {c.Page} 页图表",c.Page,c.Dpi);Refresh(p.Id);});await Task.CompletedTask;
    }
    void DemoClick(object s,RoutedEventArgs e)=>Run(()=>
    {
        Change();var image=Imaging.Demo();var d=new Document{Name="示例 · 三组响应曲线",Title="PlotPick 内置合成示例",SourceAsset=project.AddAsset(image,".png")};project.Documents.Add(d);var p=AddPlot(d,image,"响应随时间变化");
        p.Axes=new(){Calibrated=true,X=new(){A=new(150,750),B=new(1250,750),Min=0,Max=10,Label="Time (s)"},Y=new(){A=new(150,750),B=new(150,100),Min=0,Max=100,Label="Response (%)"}};
        p.Options.Roi=new(155,105,1090,640);p.Series=new[]{"#2563EB","#0D9488","#F59E0B"}.Select((color,i)=>new Series{Name=new[]{"对照组","实验组 A","实验组 B"}[i],Color=color,Marker=i}).ToList();project.Name="示例研究 · 响应曲线";Refresh(p.Id);Inspector.SelectedIndex=1;state.Status="示例已标定，已配置三条曲线。点击“预览选中曲线”体验多系列提取。";
    });
    void PasteClick(object s,RoutedEventArgs e)=>Run(()=>
    {
        if(state.Busy)return;if(!Clipboard.ContainsImage()){state.Status="剪贴板中没有图片。";return;}var bitmap=Clipboard.GetImage();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);var image=stream.ToArray();Change();var d=new Document{Name="粘贴截图 "+DateTime.Now.ToString("HH:mm"),SourceAsset=project.AddAsset(image,".png")};project.Documents.Add(d);var p=AddPlot(d,image,"截图图表");Refresh(p.Id);
    });
    async void WindowDrop(object s,DragEventArgs e){if(e.Data.GetData(DataFormats.FileDrop) is string[] paths)foreach(var path in paths)await OpenPath(path);}
    bool Save(bool saveAs=false)
    {
        if(state.Busy||!PersistSettings())return false;if(ExportPanel.Visibility==Visibility.Visible&&!ExportPanel.CommitProfile())return false;try
        {
            string? path=projectPath;if(saveAs||path is null){var d=new SaveFileDialog{Filter="PlotPick 项目|*.plotpick",FileName=project.Name+".plotpick"};if(d.ShowDialog()!=true)return false;path=d.FileName;}
            ProjectStore.Save(project,path);projectPath=path;dirty=false;DeleteRecovery();state.SaveStatus="已保存 · "+DateTime.Now.ToString("HH:mm");state.Status="项目已保存，源文件已包含在项目中。";return true;
        }catch(Exception ex){Fail(ex);return false;}
    }
    void SaveClick(object s,RoutedEventArgs e)=>Save(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    void DeleteRecovery(){try{if(File.Exists(RecoveryPath))File.Delete(RecoveryPath);if(File.Exists(RecoveryPath+".bak"))File.Delete(RecoveryPath+".bak");}catch{}}
    async Task AutoSave()
    {
        if(!dirty||state.Busy||saving||project.Documents.Count==0)return;saving=true;
        try{var clone=Json.Clone(project);clone.Assets=new(project.Assets);var path=RecoveryPath;await Task.Run(()=>ProjectStore.Save(clone,path));state.SaveStatus="恢复快照 · "+DateTime.Now.ToString("HH:mm:ss")+" · 项目尚未保存";}catch(Exception ex){state.Status="恢复快照未写入："+ex.Message;}finally{saving=false;}
    }
    bool CanReplace()
    {
        if(!dirty)return true;var result=MessageBox.Show(this,"当前项目尚未保存，是否保存？","图拾",MessageBoxButton.YesNoCancel);return result==MessageBoxResult.No||result==MessageBoxResult.Yes&&Save();
    }
    void WindowClosing(object? s,CancelEventArgs e){if(state.Busy||saving){e.Cancel=true;state.Status="请先取消或等待当前任务完成。";return;}if(!CanReplace()){e.Cancel=true;return;}autosave.Stop();DeleteRecovery();}
    void UndoClick(object s,RoutedEventArgs e){if(state.Busy||!history.CanUndo)return;project=history.Undo(project);dirty=true;revision++;ClearPreview();Refresh();state.SaveStatus="已撤销 · 尚未保存";}
    void RedoClick(object s,RoutedEventArgs e){if(state.Busy||!history.CanRedo)return;project=history.Redo(project);dirty=true;revision++;ClearPreview();Refresh();state.SaveStatus="已重做 · 尚未保存";}
    void AddSeriesClick(object s,RoutedEventArgs e)
    {
        if(!Ready()||!PersistSettings())return;Change();var colors=new[]{"#2563EB","#0D9488","#F59E0B","#E11D48","#7C3AED"};var n=plot!.Series.Count;var added=new Series{Name=$"系列 {n+1}",Color=colors[n%colors.Length],Marker=n%3};plot.Series.Add(added);Refresh(seriesId:added.Id,image:false);Inspector.SelectedIndex=1;Canvas.Tool="color";ToolHint.Text="点击新曲线的颜色，然后预览当前曲线。";UpdateNavigation();
    }
    void ReadSeriesSettings()
    {
        if(series is null||plot is null)return;if(RightAxis.IsChecked==true&&plot.Axes.RightY is null)throw new ArgumentException("请先标定右 Y 轴。");SKColor.Parse(Color.Text);
        series.Name=SeriesName.Text.Trim();if(series.Name.Length==0)series.Name="未命名系列";series.Color=Color.Text;series.RightAxis=RightAxis.IsChecked==true;series.Visible=SeriesVisible.IsChecked==true;series.Locked=SeriesLocked.IsChecked==true;series.Marker=Marker.SelectedIndex;series.Dashed=Dashed.IsChecked==true;
        if(series.Kind!=SeriesKind.Derived)series.Kind=(SeriesKind)SeriesType.SelectedIndex;
    }
    void SeriesSettingsClick(object s,RoutedEventArgs e)=>Run(()=>{if(!Ready()||!PersistSettings())return;MarkDerivedStale();Refresh(image:false);});
    Axis ReadAxis(bool x)
    {
        var axis=new Axis{Scale=(AxisScale)(x?XScale.SelectedIndex:YScale.SelectedIndex),Label=x?XLabel.Text:YLabel.Text,DateFormat=DateFormat.Text};axis.Min=axis.Parse(x?XMin.Text:YMin.Text);axis.Max=axis.Parse(x?XMax.Text:YMax.Text);return axis;
    }
    void ApplyAxes(bool positions)
    {
        if(plot is null)return;var axes=Json.Clone(plot.Axes);bool right=YAxisSide.SelectedIndex==1;if(right&&!plot.Axes.Calibrated)throw new ArgumentException("请先完成左侧坐标标定。");var x=ReadAxis(true);var y=ReadAxis(false);
        if(positions){x.A=anchors[0];x.B=anchors[1];y.A=anchors[2];y.B=anchors[3];}
        else{x.A=axes.X.A;x.B=axes.X.B;var old=right?axes.RightY??throw new ArgumentException("请先完成右轴四点标定。"):axes.Y;y.A=old.A;y.B=old.B;}
        axes.X=x;if(right)axes.RightY=y;else axes.Y=y;axes.Validate(right);axes.Calibrated=true;
        Change();plot.Axes=axes;foreach(var ds in plot.Series.Where(q=>q.Kind==SeriesKind.Derived))ds.Stale=true;
        if(right&&series is not null)series.RightAxis=true;anchors.Clear();Canvas.Anchors=[];Canvas.Tool="select";ToolHint.Text="滚轮缩放 · 空格拖动 · F 适应窗口";Refresh(image:false);state.Status="坐标标定已更新，原图中的取数位置保持不变。";
    }
    void ApplyAxesClick(object s,RoutedEventArgs e)=>Run(()=>{if(Ready())ApplyAxes(false);});
    void CalibrateClick(object s,RoutedEventArgs e)=>Run(()=>{if(!Ready())return;ReadAxis(true);ReadAxis(false);anchors.Clear();Canvas.Anchors=anchors;Canvas.Tool="anchor";ToolHint.Text="依次选择四个已知刻度 · Esc 取消";UpdateCalibrationPrompt();state.Status="可先在右侧输入数值，再依次点击四个刻度。";});
    void AxisSideChanged(object s,SelectionChangedEventArgs e){if(!IsInitialized||refreshing||plot is null)return;LoadInspector();}
    void CheckPointClick(object s,RoutedEventArgs e){if(!Ready(true))return;Canvas.Tool="check";ToolHint.Text="点击一个已知坐标位置，检查标定残差";}
    void PixelClicked(Vec p)=>Run(()=>
    {
        if(!Ready())return;if(!Canvas.ContainsPixel(p))return;
        switch(Canvas.Tool)
        {
            case "anchor":
                anchors.Add(p);Canvas.Anchors=anchors;if(anchors.Count==4){try{ApplyAxes(true);}catch{anchors.Clear();Canvas.Anchors=[];Canvas.Tool="select";throw;}}
                else ToolHint.Text=$"标定 {anchors.Count+1}/4：点击 "+new[]{"X 第一个刻度","X 第二个刻度","Y 第一个刻度","Y 第二个刻度"}[anchors.Count];break;
            case "insert":
            case "point":
                if(!Ready(true)||series!.Locked||series.Kind==SeriesKind.Derived)return;
                if(SnapPoints.IsChecked==true)
                {
                    var target=SKColor.Parse(Color.Text);var candidates=new List<Vec>();
                    for(int dy=-8;dy<=8;dy++)for(int dx=-8;dx<=8;dx++){var q=new Vec(p.X+dx,p.Y+dy);var sampledColor=Canvas.ColorAt(q);if(Math.Pow(sampledColor.Red-target.Red,2)+Math.Pow(sampledColor.Green-target.Green,2)+Math.Pow(sampledColor.Blue-target.Blue,2)<Tolerance.Value*Tolerance.Value)candidates.Add(q);}
                    if(candidates.Count>0){var nearest=candidates.MinBy(q=>(q-p).Length);var band=candidates.Where(q=>Math.Abs(q.X-nearest.X)<=1&&Math.Abs(q.Y-nearest.Y)<=6).ToList();p=new(nearest.X,band.Average(q=>q.Y));}
                }
                if(Canvas.Tool=="insert")InsertPoint(p);else{Change();MarkDerivedStale();PointEditing.Insert(series,series.Points.Count,p);RefreshRows();}break;
            case "multi-color":AddPickedSeries(p);break;
            case "color":var c=Canvas.ColorAt(p);Color.Text=$"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";PersistSettings();Canvas.Tool="select";state.Status="已采样颜色，点击预览当前曲线。";break;
            case "seed":Change();series!.Extraction!.Seed=p;state.Status=$"追踪起点：{p.X:F0}, {p.Y:F0}";Canvas.Tool="select";break;
            case "baseline":Change();series!.Extraction!.Baseline=SeriesType.SelectedIndex==3?p.X:p.Y;state.Status="柱状基线已设置。";Canvas.Tool="select";break;
            case "check":
                if(!Ready(true))return;var values=Dialogs.Form(this,"标定检查点",("已知 X",plot!.Axes.X.Format(plot.Axes.ToData(p).X)),("已知 Y",plot.Axes.Y.Format(plot.Axes.ToData(p).Y)));if(values is not null){var expected=new Vec(plot.Axes.X.Parse(values[0]),plot.Axes.Y.Parse(values[1]));Change();plot.Axes.Checks.Add(new(p,expected));state.Status=$"检查残差：{(plot.Axes.ToPixel(expected)-p).Length:F3} px";}Canvas.Tool="select";break;
            case "error":
                var point=series!.Points.FirstOrDefault(q=>q.Id==errorPoint);if(point is null)return;Change();point.Error??=new();switch(errorDirection){case "X低":point.Error.XLow=p;break;case "X高":point.Error.XHigh=p;break;case "Y低":point.Error.YLow=p;break;default:point.Error.YHigh=p;break;}point.Origin=PointOrigin.Corrected;point.Review=ReviewState.Pending;Canvas.Tool="select";RefreshRows();break;
        }
        UpdateNavigation();Canvas.InvalidateVisual();
    });
    void RegionSelected(Region region)=>Run(()=>
    {
        if(!Ready())return;region=new(Math.Clamp(region.X,0,plot!.Width-1),Math.Clamp(region.Y,0,plot.Height-1),Math.Min(region.Width,plot.Width-Math.Max(0,region.X)),Math.Min(region.Height,plot.Height-Math.Max(0,region.Y)));
        if(Canvas.Tool=="crop")
        {
            var cropped=Imaging.Crop(project.Assets[plot.ImageAsset],region);Change();var doc=project.Documents.First(d=>d.Plots.Contains(plot));var copy=Json.Clone(plot);copy.Id=Guid.NewGuid();copy.Name=plot.Name+" · 裁图";copy.Crop=region;copy.ImageAsset=project.AddAsset(cropped,".png");using var bm=SKBitmap.Decode(cropped);copy.Width=bm.Width;copy.Height=bm.Height;
            Imaging.TransformGeometry(copy,p=>new(p.X-(int)region.X,p.Y-(int)region.Y));foreach(var s in copy.Series){s.Id=Guid.NewGuid();s.Points=s.Points.Where(p=>p.Pixel.X>=0&&p.Pixel.Y>=0&&p.Pixel.X<copy.Width&&p.Pixel.Y<copy.Height).ToList();}doc.Plots.Add(copy);Refresh(copy.Id);state.Status="已创建独立裁图，原图保留。";
        }
        else{Change();if(Canvas.Tool=="exclude")RegionOptions.Exclusions.Add(region);else RegionOptions.Roi=region;state.Status="识别区域已更新。";}
        Canvas.Tool="select";UpdateNavigation();Canvas.InvalidateVisual();
    });
    void PointMoved(DataPoint point,Vec pixel)=>Run(()=>{if(!Ready(true)||series!.Locked||series.Kind==SeriesKind.Derived)return;plot!.Axes.ToData(pixel,series.RightAxis);Change();MarkDerivedStale();point.MoveTo(pixel);RefreshRows();Canvas.InvalidateVisual();});
    void ToolClick(object sender,RoutedEventArgs e){if(!Ready())return;Canvas.Tool=(string)((Button)sender).Tag;manualTail=null;if(Canvas.Tool!="anchor"){anchors.Clear();Canvas.Anchors=[];}ToolHint.Text=Canvas.Tool switch{"point"=>"点击曲线取点 · 局部放大镜辅助定位","box-select"=>"拖动框选当前系列的点 · Ctrl 追加 · Delete 删除 · Esc 清除","insert"=>"绿色连线预览插入位置 · 可选择手动指定位置","roi"=>"拖动框选识别区域","exclude"=>"拖动框选排除区域","crop"=>"拖动框选，新建独立图表区域","color"=>"点击图中的曲线颜色","seed"=>"点击追踪起点（沿线双向追踪，分支处停止）","baseline"=>"点击柱状图的基线",_=>"框选或拖动数据点 · Delete 删除"};UpdateNavigation();}
    void ClearRegionsClick(object s,RoutedEventArgs e){if(!Ready())return;Change();RegionOptions.Roi=null;RegionOptions.Exclusions.Clear();series!.Extraction!.Seed=null;Canvas.InvalidateVisual();}
    ExtractionOptions ReadOptions()
    {
        var o=SeriesOptions.Get(plot!,series!);SKColor.Parse(Color.Text);o.Color=Color.Text;o.Tolerance=Tolerance.Value;o.Step=int.Parse(SampleStep.Text);o.MaxGap=int.Parse(MaxGap.Text);o.MinArea=int.Parse(MinArea.Text);o.MaxArea=int.Parse(MaxArea.Text);o.SuppressGrid=GridSuppression.IsChecked==true;o.IntensityMode=Intensity.IsChecked==true;o.DataStep=string.IsNullOrWhiteSpace(DataStep.Text)?null:double.Parse(DataStep.Text,CultureInfo.InvariantCulture);
        if(o.Step<1||o.Step>1000||o.MaxGap<0||o.MinArea<1||o.MaxArea<o.MinArea||o.DataStep is double step&&(!double.IsFinite(step)||step<=0))throw new ArgumentException("请检查采样间距、缺口与面积参数。");return o;
    }
    async void ExtractClick(object sender,RoutedEventArgs e){if(series is not null)await PreviewMany([series]);}
    void ApplyPreviewClick(object s,RoutedEventArgs e)=>Run(()=>{if(series is not null)SavePreviews([series]);});
    void ClearPreview(){previews.Clear();Canvas.Preview=[];Canvas.PreviewLayers=[];Canvas.SetMask(null);if(ExtractionInfo is not null)ExtractionInfo.Text="先预览，检查后保存结果。";Canvas.InvalidateVisual();UpdateNavigation();}
    void DiscardPreviewClick(object s,RoutedEventArgs e){if(series is not null)previews.Remove(series.Id);SyncPreviews();state.Status="当前系列预览已丢弃。";}
    void OverlayChanged(object s,RoutedEventArgs e){if(Canvas is null)return;Canvas.HideImage=HideImage.IsChecked==true;Canvas.ShowMask=ShowMask?.IsChecked==true;Canvas.InvalidateVisual();}
    async void DetectErrorsClick(object s,RoutedEventArgs e)
    {
        if(!Ready(true)||series!.Locked)return;var points=Json.Clone(series.Points);var bytes=project.Assets[plot!.ImageAsset];ExtractionOptions opts;try{opts=ReadOptions();}catch(Exception ex){Fail(ex);return;}
        await Work(async token=>{await Task.Run(()=>Extraction.DetectErrors(bytes,points,opts,token,plot.Axes,series.RightAxis),token);token.ThrowIfCancellationRequested();Change();series.Points=points;RefreshRows();Canvas.InvalidateVisual();state.Status="误差棒候选已生成，请核对后标记复核。";});
    }
    void ManualErrorsClick(object s,RoutedEventArgs e)
    {
        if(!Ready(true)||series!.Locked)return;var selected=Canvas.Selected.FirstOrDefault();if(selected==Guid.Empty){state.Status="请先选中一个数据点。";return;}
        var f=Dialogs.Form(this,"标记误差棒端点",("方向：X低 / X高 / Y低 / Y高","Y高"));if(f is null)return;if(!new[]{"X低","X高","Y低","Y高"}.Contains(f[0])){state.Status="请输入有效的端点方向。";return;}errorDirection=f[0];errorPoint=selected;Canvas.Tool="error";ToolHint.Text="点击对应误差棒的端帽位置";
    }
    void AcceptClick(object s,RoutedEventArgs e){if(!Ready(true)||series!.Locked)return;Change();foreach(var p in series.Points)p.Review=ReviewState.Accepted;RefreshRows();Canvas.InvalidateVisual();state.Status="当前系列已标记为已复核。";}
    void SortClick(object s,RoutedEventArgs e)=>Run(()=>{if(!Ready(true)||series!.Locked)return;if(!Dialogs.Confirm(this,"按 X 值重新排序会改变回折曲线的点顺序，是否继续？"))return;Change();series.Points=series.Points.OrderBy(p=>series.ValueOf(p,plot!.Axes).X).ToList();RefreshRows();Canvas.InvalidateVisual();});
    void ValidateClick(object s,RoutedEventArgs e)=>Run(()=>{if(!Ready(true))return;var result=Extraction.Validate(plot!,series!);AnalysisReport.Text=result.Count==0?"✓ 未发现重复、越界或待复核点。":string.Join("\n",result.Take(1000));Inspector.SelectedIndex=2;state.Status=$"校验完成 · {result.Count} 条提示（不会自动修改数据）";});
    int reviewCursor=-1;
    void NextIssueClick(object s,RoutedEventArgs e)
    {
        if(!Ready(true))return;var pending=series!.Points.Where(p=>p.Review==ReviewState.Pending||p.Note.Length>0).ToList();if(pending.Count==0){state.Status="当前系列没有待复核点。";return;}
        var point=pending[(++reviewCursor)%pending.Count];var row=state.Rows.FirstOrDefault(r=>r.Id==point.Id);if(row is not null){DataTable.SelectedItem=row;DataTable.ScrollIntoView(row);}Canvas.CenterOn(point.Pixel);state.Status=$"待复核 {reviewCursor%pending.Count+1}/{pending.Count}：{point.Note}";
    }
    void StatsClick(object s,RoutedEventArgs e)=>Run(()=>{if(!Ready(true))return;var summary=Analysis.Summarize(series!.Points.Select(p=>series.ValueOf(p,plot!.Axes)).ToArray());AnalysisReport.Text=$"数量：{summary.Count}\nX 范围：{summary.XMin:G8} ～ {summary.XMax:G8}\nY 范围：{summary.YMin:G8} ～ {summary.YMax:G8}\n均值：{summary.Mean:G8}\n样本标准差：{summary.StandardDeviation:G8}";});
    void AnalyzeClick(object s,RoutedEventArgs e)=>Run(()=>
    {
        if(!Ready(true))return;string method=((ComboBoxItem)AnalysisMethod.SelectedItem).Content.ToString()!;var input=series!.Points.Select(p=>series.ValueOf(p,plot!.Axes)).ToArray();var (points,report)=Analysis.Process(input,method,int.Parse(AnalysisParameter.Text));Change();
        var child=new Series{Name=series.Name+" · "+method,Kind=SeriesKind.Derived,Color="#7C3AED",ExtractSelected=false,RightAxis=series.RightAxis,ParentSeries=series.Id,Processing=report,Locked=true,Dashed=true,Points=points.Select(p=>new DataPoint{Pixel=plot!.Axes.ToPixel(p,series.RightAxis),Value=p,Origin=PointOrigin.Derived}).ToList()};plot!.Series.Add(child);Refresh(seriesId:child.Id,image:false);Inspector.SelectedIndex=2;AnalysisReport.Text=report;state.Status="已生成独立派生系列，原数据保留。";
    });
    void ExportClick(object s,RoutedEventArgs e)=>Run(()=>OpenExport());
    void CopyClick(object s,RoutedEventArgs e)=>Run(()=>OpenExport(true));
    void ChartClick(object s,RoutedEventArgs e)=>Run(()=>{if(!Ready(true))return;var d=new SaveFileDialog{Filter="PNG 图像|*.png|SVG 矢量图|*.svg|PDF 图表|*.pdf",FileName=plot!.Name};if(d.ShowDialog()!=true)return;Change();plot.ChartTitle=ChartTitle.Text;Export.Chart(plot,d.FileName);state.Status="重绘图已导出："+d.FileName;});
    async void OcrClick(object s,RoutedEventArgs e)
    {
        if(!Ready())return;var bytes=project.Assets[plot!.ImageAsset];List<OcrSuggestion> suggestions=[];
        await Work(async token=>{state.Status="本地 OCR 正在识别刻度…";suggestions=await Task.Run(()=>Ocr.Recognize(bytes,Path.Combine(AppContext.BaseDirectory,"tessdata"),token),token);state.Status=$"OCR 完成：{suggestions.Count} 个文本候选";});
        if(suggestions.Count==0)return;
        var numbers=suggestions.Where(x=>x.Numeric.HasValue).ToList();Dialogs.Text(this,"OCR 刻度候选（尚未应用）",string.Join("\n",numbers.Select((v,i)=>$"{i+1}. {v.Text} → {v.Numeric:G10}  · 分数 {v.Confidence:F0}  · 像素 {v.Bounds.X:F0},{v.Bounds.Y:F0}"))+"\n\n请人工核对负号和指数。文字位置不等于刻度线位置；标定时仍需点击实际刻度。\n正值且等比例刻度可考虑对数轴，OCR 不会自行修改轴类型。");
        var form=Dialogs.Form(this,"采用已核对的 OCR 数值",("X 第一个刻度",XMin.Text),("X 第二个刻度",XMax.Text),("Y 第一个刻度",YMin.Text),("Y 第二个刻度",YMax.Text));if(form is null)return;XMin.Text=form[0];XMax.Text=form[1];YMin.Text=form[2];YMax.Text=form[3];state.Status="刻度建议已填入面板，尚未改变正式标定。";
    }
    void TableSelection(object s,SelectionChangedEventArgs e){if(tableSync)return;Canvas.Selected=DataTable.SelectedItems.OfType<PointRow>().Select(r=>r.Id).ToHashSet();manualTail=null;UpdateSelectionInfo();Canvas.InvalidateVisual();}
    void TableBeginEdit(object s,DataGridBeginningEditEventArgs e){if(!Ready(true)||series!.Locked||series.Kind==SeriesKind.Derived)e.Cancel=true;}
    void TableEdit(object s,DataGridRowEditEndingEventArgs e)
    {
        if(e.EditAction!=DataGridEditAction.Commit||e.Row.Item is not PointRow row)return;
        Dispatcher.BeginInvoke(()=>Run(()=>
        {
            if(plot is null||series is null)return;var point=series.Points.FirstOrDefault(p=>p.Id==row.Id);if(point is null)return;
            try{var value=new Vec(plot.Axes.X.Parse(row.X),plot.Axes.GetY(series.RightAxis).Parse(row.Y));var pixel=plot.Axes.ToPixel(value,series.RightAxis);Change();MarkDerivedStale();point.MoveTo(pixel);point.Category=row.Category;Canvas.InvalidateVisual();RefreshRows();}catch(Exception ex){RefreshRows();state.Status="输入未应用："+ex.Message;}
        }),DispatcherPriority.Background);
    }
    void DeleteClick(object s,RoutedEventArgs e)
    {
        if(!Ready()||!PointEditing.CanEdit(series!))return;var ids=series!.Points.Where(p=>Canvas.Selected.Contains(p.Id)).Select(p=>p.Id).ToHashSet();if(ids.Count==0)return;
        Change();MarkDerivedStale();int count=PointEditing.Delete(series!,ids);Canvas.ClearSelection();manualTail=null;RefreshRows();Canvas.InvalidateVisual();state.Status=$"已删除当前系列 {count} 个点 · 可撤销";
    }
    void TableKey(object s,KeyEventArgs e){if(e.Key==Key.Delete&&e.OriginalSource is not TextBox){DeleteClick(s,e);e.Handled=true;}}
    void MetadataClick(object s,RoutedEventArgs e){if(!Ready())return;Change();var d=project.Documents.First(d=>d.Plots.Contains(plot!));d.Title=PaperTitle.Text;d.Doi=Doi.Text;plot!.Name=PlotName.Text;plot.Figure=Figure.Text;plot.Notes=Notes.Text;Refresh(image:false);}
    void RemoveSeriesClick(object s,RoutedEventArgs e)
    {
        if(!Ready()||!Dialogs.Confirm(this,"移除当前系列？关联的派生系列会标记失效，此操作可撤销。"))return;Change();MarkDerivedStale();plot!.Series.Remove(series!);if(plot.Series.Count==0)plot.Series.Add(new());ClearPreview();Refresh(image:false);
    }
    void RemovePlotClick(object s,RoutedEventArgs e)
    {
        if(!Ready()||!Dialogs.Confirm(this,"移除当前图表及取数结果？此操作可撤销。"))return;Change();var document=project.Documents.First(d=>d.Plots.Contains(plot!));document.Plots.Remove(plot!);if(document.Plots.Count==0)project.Documents.Remove(document);ClearPreview();Refresh();
    }
    async void ImageProcessClick(object sender,RoutedEventArgs e)
    {
        if(!Ready())return;string operation=(string)((Button)sender).Tag;double angle=0;
        if(operation=="去倾斜"){var f=Dialogs.Form(this,"去倾斜",("逆时针角度（度）","0"));if(f is null||!double.TryParse(f[0],NumberStyles.Float,CultureInfo.InvariantCulture,out angle))return;}
        var source=project.Assets[plot!.ImageAsset];double finalAngle=angle;
        await Work(async token=>
        {
            var image=await Task.Run(()=>Imaging.Process(source,operation,finalAngle),token);token.ThrowIfCancellationRequested();Change();int oldW=plot.Width,oldH=plot.Height;
            if(operation=="旋转")Imaging.TransformGeometry(plot,p=>new(oldH-1-p.Y,p.X));
            else if(operation=="去倾斜"){double rad=finalAngle*Math.PI/180,co=Math.Cos(rad),si=Math.Sin(rad);Imaging.TransformGeometry(plot,p=>new(co*(p.X-oldW/2.0)+si*(p.Y-oldH/2.0)+oldW/2.0,-si*(p.X-oldW/2.0)+co*(p.Y-oldH/2.0)+oldH/2.0));}
            using var bm=SKBitmap.Decode(image);plot.Width=bm.Width;plot.Height=bm.Height;plot.ImageAsset=project.AddAsset(image,".png");plot.Processing+=$"{operation} {finalAngle:G4}°; ";ClearPreview();Refresh();state.Status="图像处理已应用，可撤销；原始资源保留。";
        });
    }
    void ReplaceImageClick(object s,RoutedEventArgs e)=>Run(()=>
    {
        if(!Ready())return;var d=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.bmp"};if(d.ShowDialog()!=true)return;var bytes=File.ReadAllBytes(d.FileName);using var bitmap=SKBitmap.Decode(bytes)??throw new InvalidDataException("图像无法解码。");Change();plot!.ImageAsset=project.AddAsset(Imaging.Png(bitmap),".png");plot.Width=bitmap.Width;plot.Height=bitmap.Height;plot.Axes.Calibrated=false;foreach(var sr in plot.Series)foreach(var p in sr.Points)p.Review=ReviewState.Pending;ClearPreview();Refresh();state.Status="源图已更换。必须重新标定并核对取数位置。";
    });
    void SaveTemplateClick(object s,RoutedEventArgs e)=>Run(()=>{if(!Ready(true))return;if(!PersistSettings())return;var d=new SaveFileDialog{Filter="PlotPick 模板|*.plottemplate",FileName=plot!.Name};if(d.ShowDialog()==true){File.WriteAllText(d.FileName,Json.Serialize(BatchTemplate.From(plot,series!)));state.Status="模板已保存。";}});
    async void BatchClick(object s,RoutedEventArgs e)
    {
        if(state.Busy)return;try
        {
            var td=new OpenFileDialog{Filter="PlotPick 模板|*.plottemplate",Title="选择识别模板"};if(td.ShowDialog()!=true)return;var template=Json.Deserialize<BatchTemplate>(File.ReadAllText(td.FileName));
            var fd=new OpenFileDialog{Filter="同版式图片|*.png;*.jpg;*.jpeg;*.bmp",Multiselect=true};if(fd.ShowDialog()!=true)return;
            bool same=MessageBox.Show(this,"所有图片的坐标刻度数值是否完全一致？\n选择“否”时仅复用位置，逐图重新核对刻度。","批处理标定",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
            var queue=new List<string>();
            await Work(async token=>
            {
                int count=0;foreach(var file in fd.FileNames)
                {
                    token.ThrowIfCancellationRequested();state.Status="批处理："+Path.GetFileName(file);
                    try
                    {
                        var bytes=await File.ReadAllBytesAsync(file,token);using var bitmap=SKBitmap.Decode(bytes)??throw new InvalidDataException("解码失败");var mapped=template.ForSize(bitmap.Width,bitmap.Height,same);
                        var result=await Task.Run(()=>Extraction.Run(bytes,template.Kind,mapped.Options,token),token);token.ThrowIfCancellationRequested();Change();var d=new Document{Name=Path.GetFileName(file),SourceAsset=project.AddAsset(bytes,Path.GetExtension(file))};project.Documents.Add(d);var p=AddPlot(d,Imaging.Png(bitmap),Path.GetFileNameWithoutExtension(file));p.FromBatch=true;p.Axes=mapped.Axes;p.Options=mapped.Options;p.Series=[new(){Name="批量提取",Kind=template.Kind,RightAxis=template.RightAxis,Color=mapped.Options.Color,Points=result.Points,Processing=Json.Serialize(mapped.Options)}];p.Notes="批处理待复核\n"+string.Join("\n",result.Warnings);queue.Add($"待复核：{d.Name} · {result.Points.Count} 点");
                    }catch(OperationCanceledException){throw;}catch(Exception ex){queue.Add($"失败：{Path.GetFileName(file)} · {ex.Message}");}
                    state.Progress=100.0*++count/fd.FileNames.Length;
                }
                Refresh();state.Status="批处理完成。所有结果进入待复核状态，可重新选择失败文件重试。";
            });
            Refresh();Dialogs.Text(this,"批处理汇总",string.Join("\n",queue));
        }catch(Exception ex){Fail(ex);}
    }
    void WindowKey(object sender,KeyEventArgs e)
    {
        if(e.OriginalSource is TextBox||e.OriginalSource is PasswordBox)return;
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){switch(e.Key){case Key.O:OpenClick(this,e);break;case Key.S:SaveClick(this,e);break;case Key.V:PasteClick(this,e);break;case Key.Z:UndoClick(this,e);break;case Key.Y:RedoClick(this,e);break;default:return;}e.Handled=true;return;}
        if(e.Key==Key.B){Canvas.OriginalOnly=true;Canvas.InvalidateVisual();e.Handled=true;}
        else if(e.Key==Key.F){Canvas.Fit();e.Handled=true;}else if(e.Key==Key.Escape){CancelInteraction();e.Handled=true;}else if(e.Key==Key.Delete){DeleteClick(this,e);e.Handled=true;}
        else if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down&&Canvas.IsKeyboardFocused&&series is not null&&PointEditing.CanEdit(series))
        {
            var delta=e.Key switch{Key.Left=>new Vec(-1,0),Key.Right=>new Vec(1,0),Key.Up=>new Vec(0,-1),_=>new Vec(0,1)};if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))delta*=10;var selected=series.Points.Where(p=>Canvas.Selected.Contains(p.Id)).ToList();if(selected.Count>0){Change();MarkDerivedStale();foreach(var p in selected){p.MoveTo(p.Pixel+delta);}RefreshRows();Canvas.InvalidateVisual();}e.Handled=true;
        }
    }
    void CalibrationTabClick(object s,RoutedEventArgs e){CloseExport();RightColumn.Width=new(340);Inspector.SelectedIndex=0;}
    void ExtractionTabClick(object s,RoutedEventArgs e){CloseExport();RightColumn.Width=new(340);Inspector.SelectedIndex=1;}
    void FitClick(object s,RoutedEventArgs e)=>Canvas.Fit();
    void ToggleLeft(object s,RoutedEventArgs e)=>LeftColumn.Width=new(LeftColumn.Width.Value==0?210:0);
    void ToggleRight(object s,RoutedEventArgs e)=>RightColumn.Width=new(RightColumn.Width.Value==0?340:0);
    void ToggleData(object s,RoutedEventArgs e)=>DataRow.Height=new(DataRow.Height.Value<50?195:39);
    void CancelClick(object s,RoutedEventArgs e)=>task?.Cancel();
    void HelpClick(object s,RoutedEventArgs e)=>Dialogs.Text(this,"图拾 · 快速上手","1. 打开图片或 PDF，必要时使用“裁图”框选论文中的图表。\n2. 在坐标面板输入已知刻度，开始四点标定，依次点击 X1、X2、Y1、Y2。\n3. 选择“取点”手动点击，或在提取面板吸取颜色，预览当前曲线。多曲线可依次选色添加，再预览选中曲线。\n4. 保存结果后核对数据，在表格或画布修正，标记已复核。\n5. 点击导出数据，勾选系列与列、调整顺序和名称，检查预览后导出。项目用 .plotpick 保存。\n\n滚轮缩放 · 空格拖动 · F 适应窗口 · 按住 B 闪切原图\nCtrl+O 打开 · Ctrl+V 粘贴 · Ctrl+S 保存\nCtrl+Z 撤销 · Ctrl+Y 重做 · Delete 删除所选点\n选择模式下框选，拖动点可修正位置；方向键微调。\n\n柱状图：选择方向、框选范围、指定基线，再提取。\n误差棒：先选数据点，可检测候选或手动标注四个端点。\n批处理：先保存模板，再选择图片。结果均需复核。\n\n本版本为 0.3.0 预览版。复杂交叉曲线、密集散点、OCR 指数和误差棒均需人工核对。所有处理离线进行。");
}



