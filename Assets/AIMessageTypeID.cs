using VRT.OrchestratorComm;

public static class AIMessageTypeID
{
    public static readonly MessageTypeID TID_AIObjectStateMessage =
        (MessageTypeID)1001;

    public static readonly MessageTypeID TID_AITextureSyncMessage =
        (MessageTypeID)1002;

    public static readonly MessageTypeID TID_AIPosterCreateMessage =
        (MessageTypeID)1003;

    public static readonly MessageTypeID TID_AIModelCreateMessage =
        (MessageTypeID)1004;

    public static readonly MessageTypeID TID_AIRuntimeCodeSyncMessage =
        (MessageTypeID)1005;
}