﻿using Capy64.API;
using Capy64.LuaRuntime.Extensions;
using Capy64.LuaRuntime.Handlers;
using KeraLua;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace Capy64.LuaRuntime.Libraries;
#nullable enable
public class HTTP : IPlugin
{
    private static IGame _game;
    private static HttpClient _httpClient;
    private static long _requestId;

    public static readonly string UserAgent = $"Capy64/{Capy64.Version}";

    private readonly IConfiguration _configuration;
    private readonly LuaRegister[] HttpLib = new LuaRegister[]
    {
        new()
        {
            name = "checkURL",
            function = L_CheckUrl,
        },
        new()
        {
            name = "requestAsync",
            function = L_Request,
        },
        new()
        {
            name = "websocketAsync",
            function = L_WebsocketAsync,
        },
        new(),
    };
    public HTTP(IGame game, IConfiguration configuration)
    {
        _game = game;
        _requestId = 0;
        _httpClient = new();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _configuration = configuration;
    }

    public void LuaInit(Lua L)
    {
        if (_configuration.GetValue<bool>("HTTP:Enable"))
            L.RequireF("http", Open, false);
    }

    private int Open(IntPtr state)
    {
        var L = Lua.FromIntPtr(state);
        L.NewLib(HttpLib);
        return 1;
    }

    private static readonly string[] _allowedSchemes = new[]
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        Uri.UriSchemeWs,
        Uri.UriSchemeWss,
    };
    public static bool TryGetUri(string url, out Uri uri)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out uri!) && _allowedSchemes.Contains(uri.Scheme);
    }

    private static int L_CheckUrl(IntPtr state)
    {
        var L = Lua.FromIntPtr(state);

        var url = L.CheckString(1);

        var isValid = TryGetUri(url, out _);

        L.PushBoolean(isValid);

        return 1;
    }

    private static int L_Request(IntPtr state)
    {
        var L = Lua.FromIntPtr(state);

        var request = new HttpRequestMessage();

        var url = L.CheckString(1);
        if (!TryGetUri(url, out Uri? uri) || uri is null)
        {
            L.ArgumentError(1, "invalid request url");
            return 0;
        }
        request.RequestUri = uri;

        if (L.IsTable(3)) // headers
        {
            L.PushCopy(3);
            L.PushNil();

            while (L.Next(-2))
            {
                L.PushCopy(-2);

                var k = L.CheckString(-1);
                if (L.IsStringOrNumber(-2))
                {
                    var v = L.ToString(-2);

                    request.Headers.Add(k, v);
                }
                else if (L.IsNil(-2))
                {
                    request.Headers.Remove(k);
                }
                else
                {
                    L.ArgumentError(3, "string, number or nil expected, got " + L.TypeName(L.Type(-2)) + " in field " + k);
                }

                L.Pop(2);
            }

            L.Pop(1);
        }

        var options = new Dictionary<string, object>
        {
            ["binary"] = false,
        };

        if (L.IsTable(4)) // other options?
        {
            L.PushCopy(4);
            L.PushNil();

            while (L.Next(-2))
            {
                L.PushCopy(-2);
                var k = L.CheckString(-1);

                switch (k)
                {
                    case "method":
                        options["method"] = L.CheckString(-2);
                        break;
                    case "binary":
                        options["binary"] = L.IsBoolean(-2) ? L.ToBoolean(-2) : false;
                        break;
                }

                L.Pop(2);
            }

            L.Pop(1);
        }

        if (!L.IsNoneOrNil(2))
        {
            if ((bool)options["binary"])
            {
                request.Content = new ByteArrayContent(L.CheckBuffer(2));
            }
            else
            {
                request.Content = new StringContent(L.CheckString(2));
            }
        }

        request.Method = options.TryGetValue("method", out var value)
                    ? new HttpMethod((string)value)
                    : request.Content is not null ? HttpMethod.Post : HttpMethod.Get;

        var requestId = _requestId++;

        var reqTask = _httpClient.SendAsync(request);
        reqTask.ContinueWith(async (task) =>
        {

            if (task.IsFaulted || task.IsCanceled)
            {
                _game.LuaRuntime.PushEvent("http_failure", requestId, task.Exception?.Message);
                return;
            }

            var response = await task;

            var stream = await response.Content.ReadAsStreamAsync();

            IHandle handler;
            if ((bool)options["binary"])
                handler = new BinaryReadHandle(stream);
            else
                handler = new ReadHandle(stream);

            _game.LuaRuntime.PushEvent("http_response", L =>
            {
                // arg 1, request id
                L.PushInteger(requestId);

                // arg 2, response data
                L.NewTable();

                L.PushString("success");
                L.PushBoolean(response.IsSuccessStatusCode);
                L.SetTable(-3);

                L.PushString("statusCode");
                L.PushNumber((int)response.StatusCode);
                L.SetTable(-3);

                L.PushString("reasonPhrase");
                L.PushString(response.ReasonPhrase);
                L.SetTable(-3);

                L.PushString("headers");
                L.NewTable();

                foreach (var header in response.Headers)
                {
                    L.PushString(header.Key);
                    L.PushArray(header.Value.ToArray());
                    L.SetTable(-3);
                }

                L.SetTable(-3);

                handler.Push(L, false);

                return 2;
            });

            //_game.LuaRuntime.PushEvent("http_response", requestId, response.IsSuccessStatusCode, content, (int)response.StatusCode, response.ReasonPhrase);
        });

        L.PushInteger(requestId);

        return 1;
    }

    private static int L_WebsocketAsync(IntPtr state)
    {
        var L = Lua.FromIntPtr(state);

        var url = L.CheckString(1);
        if (!TryGetUri(url, out var uri))
        {
            L.ArgumentError(1, "invalid request url");
            return 0;
        }

        var requestId = _requestId++;

        var wsClient = new ClientWebSocket();

        wsClient.Options.SetRequestHeader("User-Agent", UserAgent);

        if (L.IsTable(2)) // headers
        {
            L.PushCopy(2);
            L.PushNil();

            while (L.Next(-2))
            {
                L.PushCopy(-2);

                var k = L.CheckString(-1);
                if (L.IsStringOrNumber(-2))
                {
                    var v = L.ToString(-2);

                    wsClient.Options.SetRequestHeader(k, v);
                }
                else if (L.IsNil(-2))
                {
                    wsClient.Options.SetRequestHeader(k, null);
                }
                else
                {
                    L.ArgumentError(3, "string, number or nil expected, got " + L.TypeName(L.Type(-2)) + " in field " + k);
                }

                L.Pop(2);
            }

            L.Pop(1);
        }


        var connectTask = wsClient.ConnectAsync(uri, CancellationToken.None);
        connectTask.ContinueWith(async task =>
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                _game.LuaRuntime.PushEvent("websocket_failure", requestId, task.Exception?.Message);
                return;
            }

            await task;

            var handle = new WebSocketHandle(wsClient, requestId, _game);

            _game.LuaRuntime.PushEvent("websocket_connect", L =>
            {
                L.PushInteger(requestId);

                handle.Push(L, true);

                return 2;
            });

            var buffer = new byte[4096];
            var builder = new StringBuilder();
            while (wsClient.State == WebSocketState.Open)
            {
                var result = await wsClient.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("Closing");
                    await wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    _game.LuaRuntime.PushEvent("websocket_close", requestId);
                    return;
                }
                else
                {
                    var data = Encoding.ASCII.GetString(buffer, 0, result.Count);
                    builder.Append(data);
                }

                if (result.EndOfMessage)
                {
                    _game.LuaRuntime.PushEvent("websocket_message", requestId, builder.ToString());
                    builder.Clear();
                }
            }
        });

        L.PushInteger(requestId);

        return 1;
    }
}
