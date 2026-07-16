using System.Text.Json;
using System.Text.Json.Serialization;
using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Maui.Requests;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = false,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(DeferredNavigationRequestStoreSnapshot))]
internal sealed partial class AppNavJsonSerializerContext : JsonSerializerContext;
