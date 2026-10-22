using System.Windows;
using System.Windows.Controls;
using PlotPick.Core;

namespace PlotPick.App;
public partial class MainWindow
{
    readonly Dictionary<(Guid Project,Guid Plot),CanvasView> views=[];
    (Guid Project,Guid Plot)? viewKey;
    Guid? manualTail;
    void RememberView(){if(viewKey is {} key)views[key]=Canvas.View;}
    void RestorePlotView(bool image)
    {
        if(plot is null){viewKey=null;return;}
        var key=(project.Id,plot.Id);if(image||viewKey!=key)Canvas.SetImage(project.Assets[plot.ImageAsset],false);
        if(views.TryGetValue(key,out var view))Canvas.RestoreView(view);else Canvas.Fit();viewKey=key;
    }
    void UpdateSelectionInfo()
    {
        if(SelectionCountText is null)return;SelectionCountText.Text=$"已选 {Canvas.Selected.Count} 个点";
        DeleteSelectedButton.IsEnabled=!state.Busy&&series is not null&&PointEditing.CanEdit(series)&&Canvas.Selected.Count>0;
    }
    InsertionPosition GetInsertion(Vec pixel)
    {
        if(series is null||plot is null||!PointEditing.CanEdit(series))return new(-1,"当前系列不可编辑");
        if(InsertMode.SelectedIndex==0)return PointEditing.FindInsertion(series,pixel,Canvas.View.Scale);
        if(manualTail is Guid tail){int i=series.Points.FindIndex(p=>p.Id==tail);if(i>=0)return new(i+1,"继续插入到上一补点之后");}
        if(InsertMode.SelectedIndex==3)return new(0,"插入到系列开头");
        if(InsertMode.SelectedIndex==4)return new(series.Points.Count,"追加到系列结尾");
        if(Canvas.Selected.Count!=1)return new(-1,"先用选择工具或表格选中一个参考点");
        int index=series.Points.FindIndex(p=>Canvas.Selected.Contains(p.Id));
        return index<0?new(-1,"请重新选择参考点"):new(index+(InsertMode.SelectedIndex==2?1:0),$"插入第 {index+1} 点"+(InsertMode.SelectedIndex==2?"之后":"之前"));
    }
    void InsertModeChanged(object s,SelectionChangedEventArgs e)
    {
        if(!IsInitialized||Canvas is null||refreshing)return;manualTail=null;if(plot is null)return;Canvas.Tool="insert";UpdateNavigation();
        ToolHint.Text=InsertMode.SelectedIndex==0?"沿当前曲线补点 · 绿色连线预览插入位置":"选择参考点后切回补点；开头/结尾无需参考点";
    }
    void InsertPoint(Vec pixel)
    {
        if(!Ready(true)||series is null)return;var position=GetInsertion(pixel);if(!position.Valid){state.Status=position.Message;return;}
        plot!.Axes.ToData(pixel,series.RightAxis);Change();MarkDerivedStale();var added=PointEditing.Insert(series,position.Index,pixel);
        if(InsertMode.SelectedIndex!=0)manualTail=added.Id;Canvas.Selected=[added.Id];RefreshRows();SyncTableSelection();
        state.Status=$"已补入第 {position.Index+1} 个点 · 手动 / 待检查 · 可撤销";
    }
    void SyncTableSelection()
    {
        tableSync=true;DataTable.SelectedItems.Clear();foreach(var row in state.Rows.Where(r=>Canvas.Selected.Contains(r.Id)))DataTable.SelectedItems.Add(row);tableSync=false;UpdateSelectionInfo();
    }
    void UpdateCalibrationPrompt()
    {
        if(Canvas.Tool!="anchor"){CalibrationBanner.Visibility=Visibility.Collapsed;Canvas.InspectionTitle="";return;}
        int index=Math.Min(anchors.Count,3);string axis=index<2?"X":YAxisSide.SelectedIndex==1?"右 Y":"Y";
        string title=$"{index+1}/4：{axis} 轴{(index%2==0?"起点（第一个已知刻度）":"终点（第二个已知刻度）")}";
        string value=new[]{XMin.Text,XMax.Text,YMin.Text,YMax.Text}[index];
        CalibrationPrompt.Text=title+" · 数值 "+value;CalibrationBanner.Visibility=Visibility.Visible;
        Canvas.InspectionTitle=title+"\n数值："+value;Canvas.InvalidateVisual();
    }
    void CancelInteraction()
    {
        ClearPreview();anchors.Clear();Canvas.Anchors=[];Canvas.Tool="select";manualTail=null;Canvas.ClearSelection();task?.Cancel();UpdateNavigation();ToolHint.Text="滚轮缩放 · 空格拖动 · F 适应窗口";
    }
}
