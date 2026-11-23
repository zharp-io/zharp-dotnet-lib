using System;
using System.Threading;
using Godot;
using Zharp.Godot;

namespace ShopQuest;

public partial class Main : Node
{
    public override void _Ready()
    {
        ZharpGodot.Start(this);
        ZharpGodot.SetUser("player-7", "bo@example.com", "Bo");
        string mode = Mode();
        GD.Print("mode: ", mode);
        CallDeferred(MethodName.Finish);
        switch (mode)
        {
            case "error":
                try
                {
                    new Cart().Checkout();
                }
                catch (Exception e)
                {
                    ZharpGodot.CaptureException(e);
                }
                using (var http = new System.Net.Http.HttpClient(ZharpGodot.HttpHandler())) http.GetAsync("http://localhost:4318/scores.json").Wait();
                // Not caught: Godot logs it, and the addon's Logger reports it.
                new Cart().Pay();
                break;
            case "crash":
                // An exception on a thread of the game's own ends the process.
                new Thread(() => new Cart().Explode()).Start();
                Thread.Sleep(10000);
                break;
        }
    }

    static string Mode()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--mode=")) return arg.Substring(7);
        return "after";
    }

    async void Finish()
    {
        ZharpGodot.Flush(TimeSpan.FromSeconds(5));
        Node zharp = GetNode("/root/Zharp");
        for (int i = 0; i < 40; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            if ((int)zharp.Get("_transport").AsGodotObject().Call("pending") == 0) break;
            zharp.Call("flush");
        }
        GD.Print("done");
        GetTree().Quit();
    }
}

public class Cart
{
    public int Checkout() => throw new InvalidOperationException("Cart is empty");

    public void Pay() => throw new InvalidOperationException("Card declined");

    public void Explode() => throw new ApplicationException("Inventory corrupted on a worker thread");
}
