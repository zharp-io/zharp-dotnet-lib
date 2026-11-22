// Built without embedded sources: the error's code comes from the source bundle Zharp.Build uploaded.
using Zharp;

using var zharp = ZharpSdk.Init();
try
{
    BuildApp.Billing.Invoices.Total(Array.Empty<decimal>());
}
catch (Exception e)
{
    ZharpSdk.CaptureException(e);
}
