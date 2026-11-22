using System;
using System.Data.SqlClient;
using System.Web;
using System.Web.Routing;

namespace AspNetApp
{
    public class Global : HttpApplication
    {
        protected void Application_Start()
        {
            RouteTable.Routes.Add("hello", new Route("hello", new Handler(ctx => ctx.Response.Write("hello"))));
            RouteTable.Routes.Add("users", new Route("users/{id}", new Handler(ctx => ctx.Response.Write("user " + ctx.Request.RequestContext.RouteData.Values["id"]))));
            RouteTable.Routes.Add("boom", new Route("boom", new Handler(_ => Checkout.Run(0))));
            RouteTable.Routes.Add("db", new Route("db/fail", new Handler(_ => Orders.Load())));
        }
    }

    public static class Checkout
    {
        public static void Run(int quantity)
        {
            if (quantity <= 0) throw new InvalidOperationException("Classic cart is empty");
        }
    }

    public static class Orders
    {
        public static void Load()
        {
            using (var connection = new SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true"))
            {
                connection.Open();
                using (var command = new SqlCommand("SELECT * FROM no_such_table", connection)) command.ExecuteScalar();
            }
        }
    }

    sealed class Handler : IRouteHandler, IHttpHandler
    {
        readonly Action<HttpContext> run;
        public Handler(Action<HttpContext> run) { this.run = run; }
        public IHttpHandler GetHttpHandler(RequestContext requestContext) => this;
        public bool IsReusable => true;
        public void ProcessRequest(HttpContext context) => run(context);
    }
}
