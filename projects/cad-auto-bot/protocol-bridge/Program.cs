using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sproto;
using SprotoType;

var rpc = new SprotoRpc(Protocol.Instance);
var requestEncoder = rpc.Attach(Protocol.Instance);
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
};
var payloadJsonOptions = new JsonSerializerOptions(jsonOptions);
payloadJsonOptions.Converters.Add(new Int64StringConverter());

string? line;
while ((line = Console.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    BridgeResponse response;
    try
    {
        var command = JsonSerializer.Deserialize<BridgeCommand>(line, jsonOptions)
            ?? throw new InvalidOperationException("命令不能为空");
        response = command.Op switch
        {
            "encode" => Encode(command),
            "decode" => Decode(command),
            "encodeGate" => EncodeGate(command),
            "decodeGate" => DecodeGate(command),
            "ping" => new BridgeResponse(true, command.Id, Data: new { ready = true }),
            _ => throw new InvalidOperationException($"未知操作: {command.Op}"),
        };
    }
    catch (Exception error)
    {
        response = new BridgeResponse(false, null, Error: error.Message);
    }

    Console.WriteLine(JsonSerializer.Serialize(response, jsonOptions));
}

BridgeResponse Encode(BridgeCommand command)
{
    if (string.IsNullOrWhiteSpace(command.Type))
        throw new InvalidOperationException("encode 需要 type");
    var type = ResolveProtocolType(command.Type);
    var request = Activator.CreateInstance(type) as SprotoTypeBase
        ?? throw new InvalidOperationException($"无法创建协议类型: {command.Type}");
    ApplyFields(request, command.Fields);
    var packed = requestEncoder.Invoke(request, command.Session);
    return new BridgeResponse(
        true,
        command.Id,
        Data: new
        {
            type = command.Type,
            tag = request.Tag(),
            session = command.Session,
            packedBase64 = Convert.ToBase64String(packed),
        });
}

BridgeResponse Decode(BridgeCommand command)
{
    if (string.IsNullOrWhiteSpace(command.PackedBase64))
        throw new InvalidOperationException("decode 需要 packedBase64");
    var packed = Convert.FromBase64String(command.PackedBase64);
    var info = rpc.Dispatch(packed);
    var payload = info.type == SprotoRpc.RpcType.REQUEST ? info.requestObj : info.responseObj;
    var payloadJson = payload == null
        ? (JsonElement?)null
        : JsonSerializer.SerializeToElement(payload, payload.GetType(), payloadJsonOptions);
    return new BridgeResponse(
        true,
        command.Id,
        Data: new
        {
            rpcType = info.type.ToString(),
            tag = info.tag,
            session = info.session,
            protocolType = payload?.GetType().FullName,
            payload = payloadJson,
        });
}

BridgeResponse EncodeGate(BridgeCommand command)
{
    if (command.GateSession is null)
        throw new InvalidOperationException("encodeGate 需要 gateSession");
    if (string.IsNullOrWhiteSpace(command.Type))
        throw new InvalidOperationException("encodeGate 需要 type");
    var type = ResolveProtocolType(command.Type);
    var request = Activator.CreateInstance(type) as SprotoTypeBase
        ?? throw new InvalidOperationException($"无法创建协议类型: {command.Type}");
    ApplyFields(request, command.Fields);
    var businessPacket = requestEncoder.Invoke(request, command.Session);
    var gate = new GateMessage.request
    {
        content = new List<MessageContent>
        {
            new() { networkMessage = businessPacket },
        },
    };
    var gatePacket = requestEncoder.Invoke(gate, command.GateSession);
    return new BridgeResponse(
        true,
        command.Id,
        Data: new
        {
            type = command.Type,
            tag = request.Tag(),
            session = command.Session,
            gateSession = command.GateSession,
            packedBase64 = Convert.ToBase64String(gatePacket),
        });
}

BridgeResponse DecodeGate(BridgeCommand command)
{
    if (string.IsNullOrWhiteSpace(command.PackedBase64))
        throw new InvalidOperationException("decodeGate 需要 packedBase64");
    var packed = Convert.FromBase64String(command.PackedBase64);
    var outer = rpc.Dispatch(packed);
    var contents = outer.requestObj switch
    {
        GateMessage.request request => request.content,
        _ => (outer.responseObj as GateMessage.response)?.content,
    } ?? new List<MessageContent>();
    var messages = new List<object>();
    foreach (var content in contents)
    {
        var errorJson = content.HasError && content.error != null
            ? JsonSerializer.SerializeToElement(content.error, content.error.GetType(), payloadJsonOptions)
            : (JsonElement?)null;
        if (content.networkMessage == null)
        {
            if (errorJson != null)
            {
                messages.Add(new
                {
                    rpcType = "ERROR",
                    tag = (int?)null,
                    session = (long?)null,
                    protocolType = (string?)null,
                    payload = (JsonElement?)null,
                    error = errorJson,
                });
            }
            continue;
        }
        var inner = rpc.Dispatch(content.networkMessage);
        var payload = inner.type == SprotoRpc.RpcType.REQUEST ? inner.requestObj : inner.responseObj;
        var payloadJson = payload == null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(payload, payload.GetType(), payloadJsonOptions);
        messages.Add(new
        {
            rpcType = inner.type.ToString(),
            tag = inner.tag,
            session = inner.session,
            protocolType = payload?.GetType().FullName,
            payload = payloadJson,
            error = errorJson,
        });
    }
    return new BridgeResponse(
        true,
        command.Id,
        Data: new
        {
            outerRpcType = outer.type.ToString(),
            outerSession = outer.session,
            messages,
        });
}

Type ResolveProtocolType(string input)
{
    var normalized = input.Replace('/', '.').Trim();
    var split = normalized.LastIndexOf('.');
    if (split <= 0) throw new InvalidOperationException($"协议类型格式错误: {input}");
    var fullName = $"SprotoType.{normalized[..split]}+{normalized[(split + 1)..]}";
    return typeof(Protocol).Assembly.GetType(fullName)
        ?? throw new InvalidOperationException($"未找到协议类型: {input}");
}

void ApplyFields(object target, JsonElement? fields)
{
    if (fields is null || fields.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        return;
    if (fields.Value.ValueKind != JsonValueKind.Object)
        throw new InvalidOperationException("fields 必须是 JSON object");
    var targetType = target.GetType();
    foreach (var field in fields.Value.EnumerateObject())
    {
        var property = targetType.GetProperty(
            field.Name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property?.CanWrite != true)
            throw new InvalidOperationException($"{targetType.Name} 不存在可写字段 {field.Name}");
        property.SetValue(target, ConvertJson(field.Value, property.PropertyType));
    }
}

object? ConvertJson(JsonElement value, Type targetType)
{
    var nullableType = Nullable.GetUnderlyingType(targetType);
    if (nullableType != null)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        return ConvertJson(value, nullableType);
    }
    if (targetType == typeof(string)) return value.GetString() ?? string.Empty;
    if (targetType == typeof(bool)) return value.GetBoolean();
    if (targetType == typeof(long))
        return value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!)
            : value.GetInt64();
    if (targetType == typeof(int)) return value.GetInt32();
    if (targetType == typeof(double)) return value.GetDouble();
    if (targetType.IsEnum) return Enum.Parse(targetType, value.GetString()!, true);

    if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
    {
        var itemType = targetType.GetGenericArguments()[0];
        var list = (IList)Activator.CreateInstance(targetType)!;
        foreach (var item in value.EnumerateArray()) list.Add(ConvertJson(item, itemType));
        return list;
    }

    if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
    {
        var types = targetType.GetGenericArguments();
        var dictionary = (IDictionary)Activator.CreateInstance(targetType)!;
        foreach (var entry in value.EnumerateObject())
        {
            var key = Convert.ChangeType(entry.Name, types[0]);
            dictionary.Add(key!, ConvertJson(entry.Value, types[1]));
        }
        return dictionary;
    }

    var nested = Activator.CreateInstance(targetType)
        ?? throw new InvalidOperationException($"无法创建字段类型 {targetType.FullName}");
    ApplyFields(nested, value);
    return nested;
}

record BridgeCommand(
    string Op,
    string? Id,
    string? Type,
    long? Session,
    long? GateSession,
    JsonElement? Fields,
    string? PackedBase64);

record BridgeResponse(bool Ok, string? Id, object? Data = null, string? Error = null);

sealed class Int64StringConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? long.Parse(reader.GetString()!)
            : reader.GetInt64();

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
