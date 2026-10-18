using System.Text.Json.Serialization;

namespace TrebuchetLib.Sequences;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "ActionType")]
[JsonDerivedType(typeof(SequenceActionBackupServerData), "BackupServer")]
[JsonDerivedType(typeof(SequenceActionBroadcast), "Broadcast")]
[JsonDerivedType(typeof(SequenceActionDiscordWebHook), "DiscordWebhook")]
[JsonDerivedType(typeof(SequenceActionExecuteProcess), "ExecuteProcess")]
[JsonDerivedType(typeof(SequenceActionRConCommand), "RConCommand")]
[JsonDerivedType(typeof(SequenceActionSendRESTQuery), "RestRequest")]
[JsonDerivedType(typeof(SequenceActionWait), "Wait")]
[JsonDerivedType(typeof(SequenceActionWaitOffline), "WaitOffline")]
[JsonDerivedType(typeof(SequenceActionWaitOnline), "WaitOnline")]
[JsonDerivedType(typeof(SequenceMainAction), "MainAction")]
public interface ISequenceAction
{
    Task Execute(SequenceArgs args);
}