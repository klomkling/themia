using Themia.Quartz;
using Xunit;

namespace Themia.Quartz.Tests;

public class ThemiaQuartzOptionsTests
{
    [Fact] public void Authorize_DefaultsToNull_MeaningDenyAll() => Assert.Null(new ThemiaQuartzOptions().Authorize);
    [Fact] public void VirtualPathRoot_DefaultsToJobs() => Assert.Equal("/jobs", new ThemiaQuartzOptions().VirtualPathRoot);
    [Fact] public void CronExpressionOptions_DayOfWeekStartIndexZero_IsFalse() => Assert.False(new ThemiaQuartzOptions().CronExpressionOptions.DayOfWeekStartIndexZero);
}
