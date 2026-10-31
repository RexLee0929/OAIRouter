using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OAIRouter;

/// <summary>
/// Chat Completions ⇄ Responses API 双向翻译。
/// 之所以需要它：gpt-6-luna 在 /v1/chat/completions 上不允许「function tools + reasoning_effort」共存，
/// 想两者兼得只能走 /v1/responses，而 WorkBuddy 只会说 Chat Completions。
/// </summary>
public static class Translator
{
    /* ---------------- 安全取值：JsonNode 可能是字符串、数字、对象或数组 ---------------- */

    /// <summary>取字符串；非字符串返回空串（绝不抛异常）。</summary>
    internal static string Str(JsonNode? node)
    {
        if (node is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (value.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "";
    }

    private static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<double>(out var d)) return (int)d;
        if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    internal static string FallbackId(JsonNode? item)
    {
        var id = Str(item?["call_id"]);
        return id.Length > 0 ? id : Str(item?["id"]);
    }

    /// <summary>
    /// 取消息正文。content 可能是字符串，也可能是分片数组
    /// （[{type:"text",text:"..."}] 或 ["..."]），assistant 历史几乎都是数组形式。
    /// </summary>
    private static string ContentText(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "";
            case JsonValue value when value.TryGetValue<string>(out var s):
                return s;
            case JsonArray parts:
                var sb = new System.Text.StringBuilder();
                foreach (var part in parts)
                {
                    if (part is JsonValue pv && pv.TryGetValue<string>(out var ps)) { sb.Append(ps); continue; }
                    var t = Str(part?["text"]);
                    if (t.Length > 0) sb.Append(t);
                }
                return sb.ToString();
            default:
                return "";
        }
    }

    private static readonly Dictionary<string, string?> EffortMap = new()
    {
        ["none"] = null,
        ["minimal"] = "minimal",
        ["low"] = "low",
        ["medium"] = "medium",
        ["high"] = "high",
        ["xhigh"] = "high",
        ["max"] = "high",
    };

    public static JsonObject BuildResponsesRequest(JsonObject chat, string model, int minOutputTokens = 16, int maxOutputTokens = 0)
    {
        var input = new JsonArray();
        var systemParts = new List<string>();

        if (chat["messages"] is JsonArray messages)
        {
            foreach (var node in messages)
            {
                if (node is not JsonObject m) continue;
                var role = Str(m["role"]);

                if (role is "system" or "developer")
                {
                    var sysText = ContentText(m["content"]);
                    if (sysText.Length > 0) systemParts.Add(sysText);
                    continue;
                }

                if (role == "user")
                {
                    if (m["content"] is JsonValue uv && uv.TryGetValue<string>(out var ut))
                        input.Add(new JsonObject { ["role"] = "user", ["content"] = ut });
                    else if (m["content"] is JsonArray uarr)
                    {
                        var parts = new JsonArray();
                        foreach (var p in uarr)
                        {
                            var type = Str(p?["type"]);
                            if (type == "text")
                                parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = Str(p?["text"]) });
                            else if (type == "image_url")
                            {
                                var url = Str(p?["image_url"]);
                                if (url.Length == 0) url = Str(p?["image_url"]?["url"]);
                                if (!string.IsNullOrEmpty(url))
                                    parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
                            }
                        }
                        input.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    }
                    continue;
                }

                if (role == "assistant")
                {
                    var text = ContentText(m["content"]);
                    if (!string.IsNullOrEmpty(text))
                        input.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });

                    if (m["tool_calls"] is JsonArray calls)
                    {
                        foreach (var c in calls)
                        {
                            input.Add(new JsonObject
                            {
                                ["type"] = "function_call",
                                ["call_id"] = Str(c?["id"]),
                                ["name"] = Str(c?["function"]?["name"]),
                                ["arguments"] = Str(c?["function"]?["arguments"]),
                            });
                        }
                    }
                    continue;
                }

                if (role is "tool" or "function")
                {
                    var content = m["content"] is JsonValue tv && tv.TryGetValue<string>(out var ts)
                        ? ts
                        : m["content"]?.ToJsonString() ?? "";
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = Str(m["tool_call_id"]),
                        ["output"] = content,
                    });
                }
            }
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = input,
            ["store"] = false,
            ["stream"] = Bool(chat["stream"]) ?? false,
        };

        if (systemParts.Count > 0) body["instructions"] = string.Join("\n\n", systemParts);

        if (chat["tools"] is JsonArray tools && tools.Count > 0)
        {
            var outTools = new JsonArray();
            foreach (var t in tools)
            {
                if (Str(t?["type"]) == "function" && t["function"] is JsonObject fn)
                {
                    var item = new JsonObject
                    {
                        ["type"] = "function",
                        ["name"] = Str(fn["name"]),
                        ["parameters"] = fn["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                    };
                    var desc = Str(fn["description"]);
                    if (!string.IsNullOrEmpty(desc)) item["description"] = desc;
                    if (fn["strict"] is JsonNode strict) item["strict"] = strict.DeepClone();
                    outTools.Add(item);
                }
            }
            if (outTools.Count > 0) body["tools"] = outTools;
        }

        if (chat["tool_choice"] is JsonNode tc)
        {
            if (tc is JsonValue tcv && tcv.TryGetValue<string>(out var tcs)) body["tool_choice"] = tcs;
            else if (Str(tc["function"]?["name"]) is { } tcn and not "")
                body["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = tcn };
        }

        var effortRaw = Str(chat["reasoning_effort"]);
        if (effortRaw.Length == 0) effortRaw = Str(chat["reasoning"]?["effort"]);
        if (effortRaw.Length > 0 && EffortMap.TryGetValue(effortRaw, out var effort) && effort is not null)
            body["reasoning"] = new JsonObject { ["effort"] = effort };

        var maxOut = Int(chat["max_completion_tokens"]) ?? Int(chat["max_tokens"]);
        // 客户端探测会发 max_tokens=1（低于上游下限），也可能填一个超过模型能力的值（上游报错）。
        // 这里按配置同时夹上下限：下限兜底探测值，上限防止把额度写穿。
        if (maxOut is > 0)
        {
            var value = Math.Max(Math.Max(1, minOutputTokens), maxOut.Value);
            if (maxOutputTokens > 0) value = Math.Min(value, maxOutputTokens);
            body["max_output_tokens"] = value;
        }

        if (chat["temperature"] is JsonValue) body["temperature"] = chat["temperature"]!.DeepClone();
        if (chat["top_p"] is JsonValue) body["top_p"] = chat["top_p"]!.DeepClone();

        return body;
    }

    public static JsonObject ToChatCompletion(JsonObject resp, string model)
    {
        var text = new StringBuilder();
        var toolCalls = new JsonArray();

        if (resp["output"] is JsonArray output)
        {
            foreach (var item in output)
            {
                var type = Str(item?["type"]);
                if (type == "message" && item["content"] is JsonArray content)
                {
                    foreach (var c in content)
                        text.Append(Str(c?["text"]));
                }
                else if (type == "function_call")
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = FallbackId(item),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = Str(item["name"]),
                            ["arguments"] = Str(item["arguments"]),
                        },
                    });
                }
            }
        }

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.Length > 0 ? text.ToString() : null,
        };
        if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;

        var result = new JsonObject
        {
            ["id"] = Str(resp["id"]) ?? "chatcmpl-" + Guid.NewGuid().ToString("N")[..12],
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = toolCalls.Count > 0 ? "tool_calls" : "stop",
                },
            },
        };

        if (MapUsage(resp["usage"]) is { } usage) result["usage"] = usage;
        return result;
    }

    public static JsonObject? MapUsage(JsonNode? u)
    {
        if (u is not JsonObject usage) return null;
        var inp = usage["input_tokens"]?.GetValue<int?>() ?? 0;
        var outp = usage["output_tokens"]?.GetValue<int?>() ?? 0;
        return new JsonObject
        {
            ["prompt_tokens"] = inp,
            ["completion_tokens"] = outp,
            ["total_tokens"] = usage["total_tokens"]?.GetValue<int?>() ?? inp + outp,
        };
    }
}

/// <summary>把 Responses 的 SSE 流转成 Chat Completions 的 SSE 流。</summary>
public sealed class SseTranslator
{
    private readonly string _model;
    private readonly Func<string, Task> _write;
    private readonly string _id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly StringBuilder _buffer = new();
    private readonly Dictionary<string, int> _toolIndex = new();
    private int _nextToolIndex;
    private bool _roleSent;
    private bool _finished;
    private readonly bool _wantUsage;

    /// <summary>流结束后可从上游 usage 事件里拿到的用量</summary>
    public JsonObject? Usage { get; private set; }

    public SseTranslator(string model, bool wantUsage, Func<string, Task> write)
    {
        _model = model;
        _wantUsage = wantUsage;
        _write = write;
    }

    private JsonObject Chunk(JsonObject delta, string? finish = null)
    {
        return new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion.chunk",
            ["created"] = _created,
            ["model"] = _model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finish,
                },
            },
        };
    }

    private async Task EnsureRole()
    {
        if (_roleSent) return;
        _roleSent = true;
        await Send(Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }));
    }

    private async Task Send(JsonObject obj) => await _write($"data: {obj.ToJsonString()}\n\n");

    public async Task FeedAsync(string chunk)
    {
        _buffer.Append(chunk);
        while (true)
        {
            var text = _buffer.ToString();
            var idx = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (idx < 0) break;

            var raw = text[..idx];
            _buffer.Remove(0, idx + 2);

            string name = "";
            var data = new StringBuilder();
            foreach (var line in raw.Split('\n'))
            {
                if (line.StartsWith("event:")) name = line[6..].Trim();
                else if (line.StartsWith("data:"))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line[5..].Trim());
                }
            }
            if (data.Length == 0) continue;

            JsonObject? evt;
            try { evt = JsonNode.Parse(data.ToString()) as JsonObject; }
            catch { continue; }
            if (evt is null) continue;

            await HandleEvent(name.Length > 0 ? name : Translator.Str(evt["type"]), evt);
            if (_finished) return;
        }
    }

    private async Task HandleEvent(string name, JsonObject evt)
    {
        switch (name)
        {
            case "response.output_text.delta":
                await EnsureRole();
                await Send(Chunk(new JsonObject { ["content"] = Translator.Str(evt["delta"]) }));
                break;

            case "response.output_item.added":
                if (evt["item"] is JsonObject item && Translator.Str(item["type"]) == "function_call")
                {
                    await EnsureRole();
                    var idx = _nextToolIndex++;
                    var key = Translator.Str(item["id"]); if (key.Length == 0) key = Translator.Str(item["call_id"]); if (key.Length == 0) key = idx.ToString();
                    _toolIndex[key] = idx;
                    await Send(Chunk(new JsonObject
                    {
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = idx,
                                ["id"] = Translator.FallbackId(item),
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = Translator.Str(item["name"]),
                                    ["arguments"] = "",
                                },
                            },
                        },
                    }));
                }
                break;

            case "response.function_call_arguments.delta":
                if (Translator.Str(evt["item_id"]) is { Length: > 0 } itemId && _toolIndex.TryGetValue(itemId, out var index))
                {
                    await Send(Chunk(new JsonObject
                    {
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = index,
                                ["function"] = new JsonObject { ["arguments"] = Translator.Str(evt["delta"]) },
                            },
                        },
                    }));
                }
                break;

            case "response.completed":
                {
                    var resp = evt["response"] as JsonObject ?? new JsonObject();
                    Usage = resp["usage"] as JsonObject;
                    var hasFn = (resp["output"] as JsonArray)?.Any(o => Translator.Str(o?["type"]) == "function_call") == true;

                    if (!_roleSent)
                    {
                        var converted = Translator.ToChatCompletion(resp, _model);
                        var msg = converted["choices"]![0]!["message"]!;
                        await EnsureRole();
                        if (Translator.Str(msg["content"]) is { Length: > 0 } content)
                            await Send(Chunk(new JsonObject { ["content"] = content }));
                        if (msg["tool_calls"] is JsonArray tcs)
                        {
                            for (var i = 0; i < tcs.Count; i++)
                            {
                                await Send(Chunk(new JsonObject
                                {
                                    ["tool_calls"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["index"] = i,
                                            ["id"] = Translator.Str(tcs[i]?["id"]),
                                            ["type"] = "function",
                                            ["function"] = tcs[i]?["function"]?.DeepClone(),
                                        },
                                    },
                                }));
                            }
                        }
                    }

                    _finished = true;
                    var fin = Chunk(new JsonObject(), hasFn ? "tool_calls" : "stop");
                    if (_wantUsage && Translator.MapUsage(resp["usage"]) is { } usage) fin["usage"] = usage;
                    await Send(fin);
                    await _write("data: [DONE]\n\n");
                    break;
                }

            case "response.failed":
            case "error":
                {
                    var msg = evt["response"]?["error"]?["message"]?.GetValue<string>()
                              ?? evt["message"]?.GetValue<string>() ?? "上游返回错误";
                    await _write($"data: {new JsonObject { ["error"] = new JsonObject { ["message"] = msg, ["type"] = "upstream_error" } }.ToJsonString()}\n\n");
                    await _write("data: [DONE]\n\n");
                    _finished = true;
                    break;
                }
        }
    }

    public async Task EndAsync()
    {
        if (_finished) return;
        _finished = true;
        await Send(Chunk(new JsonObject(), "stop"));
        await _write("data: [DONE]\n\n");
    }
}
