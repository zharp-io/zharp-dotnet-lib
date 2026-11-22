using System;
using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Zharp.Internal
{
    /// <summary>
    /// Follows the caller's decision when there is one (the browser's traceparent), else samples
    /// <c>rate</c> of traces. What isn't sampled is still recorded, never dropped: errors in it are
    /// reported with their request's ids, and the log stamp links its lines.
    /// </summary>
    internal sealed class ZharpSampler : Sampler
    {
        readonly double rate;
        readonly TraceIdRatioBasedSampler ratio;

        public ZharpSampler(double rate)
        {
            this.rate = rate;
            ratio = new TraceIdRatioBasedSampler(Math.Max(0, Math.Min(1, rate)));
            Description = "ZharpSampler{" + rate + "}";
        }

        public override SamplingResult ShouldSample(in SamplingParameters p)
        {
            bool sampled;
            if (p.ParentContext.TraceId != default)
            {
                sampled = (p.ParentContext.TraceFlags & ActivityTraceFlags.Recorded) != 0 && rate > 0;
            }
            else
            {
                sampled = rate >= 1 || (rate > 0 && ratio.ShouldSample(p).Decision == SamplingDecision.RecordAndSample);
            }
            return new SamplingResult(sampled ? SamplingDecision.RecordAndSample : SamplingDecision.RecordOnly);
        }
    }
}
