using Zharp.Internal;
using Xunit;

namespace SdkTests
{
    public class SecretsTests
    {
        [Theory]
        [InlineData("Authorization")]
        [InlineData("cookie")]
        [InlineData("Set-Cookie")]
        [InlineData("X-Api-Key")]
        [InlineData("x-auth-token")]
        [InlineData("X-CSRF-Token")]
        [InlineData("x-session-id")]
        [InlineData("X-Client-Secret")]
        public void Secret_headers_are_filtered(string name) =>
            Assert.Equal("[Filtered]", Secrets.HeaderValue(name, new[] { "value" }, null));

        [Fact]
        public void Other_headers_are_kept_and_long_values_cut()
        {
            Assert.Equal("text/html, application/json", Secrets.HeaderValue("Accept", new[] { "text/html", "application/json" }, null));
            Assert.Equal(513, Secrets.HeaderValue("X-Long", new[] { new string('a', 2000) }, null).Length);
        }

        [Fact]
        public void More_names_can_be_filtered() =>
            Assert.Equal("[Filtered]", Secrets.HeaderValue("X-Tenant", new[] { "acme" }, new[] { "x-tenant" }));

        [Fact]
        public void Secret_query_values_and_user_info_leave_urls()
        {
            Assert.Equal("https://api.example.com/x?token=[Filtered]&page=2&key=[Filtered]",
                Secrets.RedactUrl("https://user:pass@api.example.com/x?token=abc&page=2&key=k1"));
            Assert.Equal("/a?b=1#frag", Secrets.RedactUrl("/a?b=1#frag"));
        }

        [Theory]
        [InlineData("password", true)]
        [InlineData("new_password", true)]
        [InlineData("cardNumber", true)]
        [InlineData("pin", true)]
        [InlineData("name", false)]
        [InlineData("pinned", false)]
        public void Secret_keys_follow_the_node_rules(string key, bool secret) =>
            Assert.Equal(secret, Secrets.IsSecretKey(key));
    }
}
