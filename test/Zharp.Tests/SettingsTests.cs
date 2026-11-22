using System;
using System.Collections.Generic;
using Zharp;
using Zharp.Internal;
using Xunit;

namespace SdkTests
{
    [Collection("environment")]
    public class SettingsTests : IDisposable
    {
        readonly Dictionary<string, string> env = new Dictionary<string, string>();
        readonly Func<string, string?> saved = Settings.EnvironmentReader;

        public SettingsTests()
        {
            Settings.EnvironmentReader = name => env.TryGetValue(name, out string? v) ? v : null;
        }

        public void Dispose() => Settings.EnvironmentReader = saved;

        [Fact]
        public void Defaults_match_the_other_sdks()
        {
            Settings s = Settings.Resolve(null, null);
            Assert.False(s.HasKey);
            Assert.Equal("https://api.zharp.io/api/v1/otlp", s.Endpoint);
            Assert.False(s.Logs);
            Assert.True(s.Tracing);
            Assert.True(s.LogCorrelation);
            Assert.True(s.CaptureHeaders);
            Assert.True(s.CaptureQueryErrors);
            Assert.True(s.CaptureSourceContext);
            Assert.Null(s.CaptureBodies);
            Assert.Equal(1.0, s.SampleRate);
        }

        [Fact]
        public void The_environment_is_read()
        {
            env["ZHARP_API_KEY"] = "zpk_1";
            env["ZHARP_ENDPOINT"] = "http://localhost:3000/api/v1/otlp/";
            env["ZHARP_LOGS"] = "true";
            env["ZHARP_TRACING"] = "off";
            env["ZHARP_ENV"] = "staging";
            env["ZHARP_CAPTURE_BODIES"] = "true";
            env["ZHARP_SAMPLE_RATE"] = "0.25";
            Settings s = Settings.Resolve(null, null);
            Assert.Equal("zpk_1", s.Key);
            Assert.Equal("http://localhost:3000/api/v1/otlp", s.Endpoint);
            Assert.True(s.Logs);
            Assert.False(s.Tracing);
            Assert.Equal("staging", s.Environment);
            Assert.Equal("errors", s.CaptureBodies);
            Assert.Equal(0.25, s.SampleRate);
        }

        [Fact]
        public void Code_wins_over_the_environment_and_aspnetcore_environment_is_the_last_resort()
        {
            env["ZHARP_KEY"] = "zpk_env";
            env["ASPNETCORE_ENVIRONMENT"] = "Production";
            Settings s = Settings.Resolve(new ZharpOptions { Key = "zpk_code", Logs = true }, null);
            Assert.Equal("zpk_code", s.Key);
            Assert.True(s.Logs);
            Assert.Equal("Production", s.Environment);
        }
    }
}
