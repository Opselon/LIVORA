namespace Livora.Server.Infrastructure.Engines.Decision;

/// <summary>
/// PURPOSE: the pipeline façade the HTTP module calls — compute state, fire rules, fuse the plan.
///          Kept trivial on purpose (the engines hold the logic); it exists so the endpoint handler
///          and the tests exercise ONE entry point, the same way the client's orchestrator does.
/// OWNER: Agent 10+11 (lane w4-p1e-engines). No AI, no IO, no clock — pure composition.
/// </summary>
public sealed class DecisionPipeline
{
    public DecisionOutput Run(DecisionInput input)
    {
        var state = EngineStateComputer.Compute(input);
        var hits = EngineRuleEvaluator.Evaluate(state, input);
        return EngineFusionPlanner.Plan(input, state, hits);
    }
}
