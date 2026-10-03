using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class StreamParsingTests
{
    private static JsonObject L(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Anthropic_output_comes_from_message_delta()
    {
        ClaudeRuntime rt = new();
        Outcome o = new();
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":9,"output_tokens":4,"cache_read_input_tokens":0,"cache_creation_input_tokens":4608}}}}"""), o);
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"assistant","message":{"id":"m1","model":"claude-haiku-4-5","usage":{"input_tokens":9,"output_tokens":4,"cache_creation_input_tokens":4608}}}"""), o);
        Usage u = rt.Parse(L(/*lang=json,strict*/ """{"type":"stream_event","event":{"type":"message_delta","usage":{"output_tokens":322}}}"""), o)!;
        Assert.Equal((9L, 322L, 4608L), (u.Input, u.Output, u.CacheWrite5m));
        Assert.Equal("claude-haiku-4-5", u.Model);
    }

    [Fact]
    public void Zai_sends_everything_in_message_delta()
    {
        ClaudeRuntime rt = new();
        Outcome o = new();
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"stream_event","event":{"type":"message_start","message":{"id":"z1","model":"glm-5.3","usage":{"input_tokens":0,"output_tokens":0}}}}"""), o);
        Usage u = rt.Parse(L(/*lang=json,strict*/ """{"type":"stream_event","event":{"type":"message_delta","usage":{"input_tokens":173,"output_tokens":13,"cache_read_input_tokens":1280}}}"""), o)!;
        Usage late = rt.Parse(L(/*lang=json,strict*/ """{"type":"assistant","message":{"id":"z1","model":"glm-5.3","usage":{"input_tokens":0,"output_tokens":0}}}"""), o)!;
        Assert.Equal((173L, 13L, 1280L), (u.Input, u.Output, u.CacheRead));
        Assert.Equal((173L, 13L, 1280L), (late.Input, late.Output, late.CacheRead));
    }

    [Fact]
    public void Claude_result_fills_the_outcome()
    {
        Outcome o = new();
        _ = new ClaudeRuntime().Parse(L(/*lang=json,strict*/ """{"type":"result","is_error":false,"result":"DONE","session_id":"s","total_cost_usd":0.0126,"num_turns":3,"permission_denials":[{}]}"""), o);
        Assert.True(o.HasResult);
        Assert.Equal(("DONE", 3L, 1L, 0.0126m), (o.Report, o.Turns, o.Denials, o.ReportedCost!.Value));
    }

    [Fact]
    public void Opencode_steps_carry_usage_and_the_last_message_is_the_report()
    {
        OpencodeRuntime rt = new();
        Outcome o = new();
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"messageID":"a","text":"thinking aloud"}}"""), o);
        Usage u = rt.Parse(L(/*lang=json,strict*/ """{"type":"step_finish","sessionID":"ses_1","part":{"id":"p1","type":"step-finish","tokens":{"input":721,"output":2343,"reasoning":445,"cache":{"write":0,"read":149184}},"cost":0}}"""), o)!;
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"messageID":"b","text":"DONE 12"}}"""), o);
        rt.Finish(o, 0);
        Assert.Equal((721L, 2343L, 445L, 149184L), (u.Input, u.Output, u.Reasoning, u.CacheRead));
        Assert.Equal(("ses_1", "DONE 12", 1L), (o.SessionId!, o.Report, o.Turns));
        Assert.True(o.HasResult);
    }

    [Fact]
    public void Opencode_denied_and_rejected_tool_calls_count_as_denials()
    {
        OpencodeRuntime rt = new();
        Outcome o = new();
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"bash","state":{"status":"error","error":"The user has specified a rule which prevents you from using this specific tool call."}}}"""), o);
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"bash","state":{"status":"error","error":"The user rejected permission to use this specific tool call."}}}"""), o);
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"bash","state":{"status":"error","error":"exit code 1"}}}"""), o);
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"bash","state":{"status":"completed"}}}"""), o);
        Assert.Equal(2L, o.Denials);
    }

    [Fact]
    public void Opencode_session_cut_off_after_a_tool_call_has_no_result()
    {
        OpencodeRuntime rt = new();
        Outcome o = new();
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"text","part":{"messageID":"a","text":"Now let me look at the fixtures."}}"""), o);
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"step_finish","part":{"id":"p1","reason":"tool-calls","tokens":{"input":1,"output":1,"reasoning":0,"cache":{"write":0,"read":0}}}}"""), o);
        rt.Finish(o, 0);
        Assert.False(o.HasResult);

        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"text","part":{"messageID":"b","text":"DONE"}}"""), o);
        _ = rt.Parse(L(/*lang=json,strict*/ """{"type":"step_finish","part":{"id":"p2","reason":"stop","tokens":{"input":1,"output":1,"reasoning":0,"cache":{"write":0,"read":0}}}}"""), o);
        rt.Finish(o, 0);
        Assert.True(o.HasResult);
    }

    [Fact]
    public void Opencode_run_without_text_has_no_result()
    {
        Outcome o = new();
        new OpencodeRuntime().Finish(o, 0);
        Assert.False(o.HasResult);
    }
}
