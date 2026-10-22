using PlotPick.Core;
using Xunit;
namespace PlotPick.Tests;
public class PointEditingTests
{
    static Series Curve(params Vec[] pixels)=>new(){Points=pixels.Select(p=>new DataPoint{Pixel=p,Origin=PointOrigin.Automatic}).ToList()};
    [Fact]public void InsertsIntoPolylineWithoutSortingX()
    {
        var s=Curve(new(0,0),new(50,0),new(50,50),new(0,50));var ids=s.Points.Select(p=>p.Id).ToArray();
        var hit=PointEditing.FindInsertion(s,new(25,50),1);Assert.Equal(3,hit.Index);var added=PointEditing.Insert(s,hit.Index,new(25,50));
        Assert.Equal(ids,s.Points.Where(p=>p.Id!=added.Id).Select(p=>p.Id));Assert.Equal(PointOrigin.Manual,added.Origin);Assert.Equal(ReviewState.Pending,added.Review);
    }
    [Theory][InlineData(1)][InlineData(2)][InlineData(.5)]public void HitRadiusUsesScreenPixels(double zoom)
    {
        var s=Curve(new(0,0),new(100,0));Assert.True(PointEditing.FindInsertion(s,new(50,11/zoom),zoom).Valid);Assert.False(PointEditing.FindInsertion(s,new(50,13/zoom),zoom).Valid);
    }
    [Fact]public void CrossingIsAmbiguousButAdjacentSegmentsAreAllowed()
    {
        var s=Curve(new(0,0),new(100,100),new(0,100),new(100,0));Assert.False(PointEditing.FindInsertion(s,new(50,50),1).Valid);
        Assert.True(PointEditing.FindInsertion(Curve(new(0,0),new(100,0),new(100,100)),new(100,0),1).Valid);
    }
    [Fact]public void NeverAutomaticallyCrossesSegmentBoundary()
    {
        var s=Curve(new(0,0),new(20,0),new(100,0),new(120,0));s.Points[2].Note=s.Points[3].Note="segment:1";
        Assert.False(PointEditing.FindInsertion(s,new(60,0),1).Valid);var p=PointEditing.Insert(s,2,new(60,0));Assert.Equal("",p.Note);Assert.Equal("segment:1",s.Points[3].Note);
    }
    [Fact]public void DenseSamplesOnOnePathAreNotSeparateBranches()
    {
        var s=Curve(Enumerable.Range(0,101).Select(i=>new Vec(i,0)).ToArray());
        Assert.Equal(51,PointEditing.FindInsertion(s,new(50.5,1),.3).Index);
    }
    [Fact]public void DenseHairpinStillReportsTwoNearbyBranches()
    {
        var points=Enumerable.Range(0,101).Select(i=>new Vec(i,0)).Concat(Enumerable.Range(0,101).Select(i=>new Vec(100-i,4))).ToArray();
        Assert.False(PointEditing.FindInsertion(Curve(points),new(50.5,2),1).Valid);
    }
    [Fact]public void ReversedXAndRepeatedXPreserveCollectionOrder()
    {
        var s=Curve(new(100,0),new(100,100),new(0,100));Assert.Equal(1,PointEditing.FindInsertion(s,new(100,50),1).Index);Assert.Equal(2,PointEditing.FindInsertion(s,new(50,100),1).Index);
    }
    [Fact]public void ManualInsertionSupportsHeadTailAndRepeatedInsertions()
    {
        var s=Curve(new(10,10),new(50,50));var head=PointEditing.Insert(s,0,new(0,0));var second=PointEditing.Insert(s,1,new(5,5));var tail=PointEditing.Insert(s,s.Points.Count,new(100,100));
        Assert.Same(head,s.Points[0]);Assert.Same(second,s.Points[1]);Assert.Same(tail,s.Points[^1]);
    }
    [Fact]public void SelectionDeletionPreservesRemainingMetadataAndHistory()
    {
        var s=Curve(new(0,0),new(10,10),new(20,20));s.Points[0].Error=new(){YHigh=new(0,-2)};var first=Json.Serialize(s.Points[0]);
        var p=new Project{Documents=[new(){Plots=[new(){Series=[s]}]}]};var history=new History();history.Push(p);
        var ids=PointEditing.Select(s,new(9,9,2,2));Assert.Single(ids);Assert.Equal(1,PointEditing.Delete(s,ids));Assert.Equal(first,Json.Serialize(s.Points[0]));
        var old=history.Undo(p);Assert.Equal(3,old.Plots.First().Series[0].Points.Count);Assert.Equal(2,history.Redo(old).Plots.First().Series[0].Points.Count);
    }
    [Theory][InlineData(SeriesKind.Curve,true)][InlineData(SeriesKind.Derived,false)]public void LockedAndDerivedSeriesAreProtected(SeriesKind kind,bool locked)
    {
        var s=Curve(new(0,0),new(10,10));s.Kind=kind;s.Locked=locked;Assert.False(PointEditing.FindInsertion(s,new(5,5),1).Valid);
        Assert.Throws<InvalidOperationException>(()=>PointEditing.Insert(s,1,new(5,5)));Assert.Throws<InvalidOperationException>(()=>PointEditing.Delete(s,new HashSet<Guid>{s.Points[0].Id}));
    }
    [Theory][InlineData(SeriesKind.Scatter)][InlineData(SeriesKind.Bars)][InlineData(SeriesKind.HorizontalBars)]public void NonCurvesRequireManualPosition(SeriesKind kind)
    {
        var s=Curve(new(0,0),new(20,20));s.Kind=kind;Assert.False(PointEditing.FindInsertion(s,new(10,10),1).Valid);PointEditing.Insert(s,1,new(10,10));Assert.Equal(3,s.Points.Count);
    }
}
