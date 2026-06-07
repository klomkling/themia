using Themia.Quartz.Dashboard.Models;
using Xunit;

namespace Themia.Quartz.Tests.Json;

/// <summary>
/// Pins the JSON produced by <see cref="TriggerPropertiesViewModel.MisfireInstructionsJson"/>
/// (now System.Text.Json) — embedded in HTML templates and consumed as a JS object. A permanent
/// compatibility pin: this exact string must not change, or the JS templates break.
/// </summary>
public sealed class MisfireInstructionsJsonTests
{
    // TriggerPropertiesViewModel.MisfireInstructionsJson is a static property computed from
    // a private static field — it is always the same value, regardless of instance state.
    private static string MisfireJson => new TriggerPropertiesViewModel().MisfireInstructionsJson;

    [Fact]
    public void MisfireInstructionsJson_ContainsAllTriggerTypeKeys()
    {
        var json = MisfireJson;

        // The JS templates use these exact string keys to look up misfire options
        Assert.Contains("\"cron\":", json);
        Assert.Contains("\"calendar\":", json);
        Assert.Contains("\"daily\":", json);
        Assert.Contains("\"simple\":", json);
    }

    [Fact]
    public void MisfireInstructionsJson_ContainsStandardMisfireLabels()
    {
        var json = MisfireJson;

        Assert.Contains("Ignore Misfire Policy", json);
        Assert.Contains("Instruction Not Set", json);
        Assert.Contains("Fire Once Now", json);
        Assert.Contains("Do Nothing", json);
    }

    [Fact]
    public void MisfireInstructionsJson_ContainsSimpleTriggerSpecificLabels()
    {
        var json = MisfireJson;

        Assert.Contains("Fire Now", json);
        Assert.Contains("Reschedule Now With Existing Repeat Count", json);
        Assert.Contains("Reschedule Now With Remaining Repeat Count", json);
        Assert.Contains("Reschedule Next With Remaining Count", json);
        Assert.Contains("Reschedule Next With Existing Count", json);
    }

    [Fact]
    public void MisfireInstructionsJson_KeysAreQuotedNumericStrings()
    {
        // The inner maps are Dictionary<int,string>; JSON object keys are always strings, so the int
        // keys serialize as quoted numeric strings ({"0":"...","-1":"..."}). The JS templates parse
        // these and use them as numeric instruction codes. This must stay stable across serializers.
        var json = MisfireJson;

        // Keys appear as quoted numeric strings within the nested objects
        // e.g. {"cron":{"0":"Instruction Not Set","-1":"Ignore Misfire Policy",...}}
        Assert.Contains("\"0\":", json);  // InstructionNotSet = 0
        Assert.Contains("\"-1\":", json); // IgnoreMisfirePolicy = -1
        Assert.Contains("\"1\":", json);  // FireNow / FireOnceNow = 1
    }

    [Fact]
    public void MisfireInstructionsJson_ExactWireFormat_MatchesExpected()
    {
        // Full exact pin. Quartz misfire instruction integer values are stable constants.
        // MisfireInstruction.IgnoreMisfirePolicy = -1, InstructionNotSet = 0
        // CronTrigger.FireOnceNow = 1, CronTrigger.DoNothing = 2
        // SimpleTrigger.FireNow = 1, RescheduleNowWithExistingRepeatCount = 2,
        //   RescheduleNowWithRemainingRepeatCount = 3, RescheduleNextWithRemainingCount = 4,
        //   RescheduleNextWithExistingCount = 5
        // KEY ORDER: Dictionary insertion order is preserved by System.Text.Json.
        // IgnoreMisfirePolicy (-1) is inserted first → serialized first.
        var json = MisfireJson;

        // Standard block: insertion order is -1, 0, 1, 2
        var standardBlock = "{\"-1\":\"Ignore Misfire Policy\",\"0\":\"Instruction Not Set\",\"1\":\"Fire Once Now\",\"2\":\"Do Nothing\"}";
        Assert.Contains($"\"cron\":{standardBlock}", json);
        Assert.Contains($"\"calendar\":{standardBlock}", json);
        Assert.Contains($"\"daily\":{standardBlock}", json);

        // Simple trigger block: insertion order -1, 0, 1, 2, 3, 4, 5
        var simpleBlock = "{\"-1\":\"Ignore Misfire Policy\",\"0\":\"Instruction Not Set\",\"1\":\"Fire Now\",\"2\":\"Reschedule Now With Existing Repeat Count\",\"3\":\"Reschedule Now With Remaining Repeat Count\",\"4\":\"Reschedule Next With Remaining Count\",\"5\":\"Reschedule Next With Existing Count\"}";
        Assert.Contains($"\"simple\":{simpleBlock}", json);
    }

    [Fact]
    public void MisfireInstructionsJson_FullString_MatchesExpected()
    {
        // Pin the complete JSON string so the STJ migration can be validated end-to-end.
        var json = MisfireJson;

        var standardBlock = "{\"-1\":\"Ignore Misfire Policy\",\"0\":\"Instruction Not Set\",\"1\":\"Fire Once Now\",\"2\":\"Do Nothing\"}";
        var simpleBlock = "{\"-1\":\"Ignore Misfire Policy\",\"0\":\"Instruction Not Set\",\"1\":\"Fire Now\",\"2\":\"Reschedule Now With Existing Repeat Count\",\"3\":\"Reschedule Now With Remaining Repeat Count\",\"4\":\"Reschedule Next With Remaining Count\",\"5\":\"Reschedule Next With Existing Count\"}";
        var expected = $"{{\"cron\":{standardBlock},\"calendar\":{standardBlock},\"daily\":{standardBlock},\"simple\":{simpleBlock}}}";

        Assert.Equal(expected, json);
    }
}
