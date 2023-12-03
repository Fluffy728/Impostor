﻿using System;
using System.Threading.Tasks;
using Impostor.Api.Net.Inner;

namespace Impostor.Server.Net.Inner.Objects.GameManager.Logic;

internal abstract class GameLogicComponent
{
    public virtual void HandleRpc(RpcCalls callId, IMessageReader reader)
    {
        throw new NotImplementedException($"Unhandled RpcCall {callId}");
    }

    public virtual bool Serialize(IMessageWriter writer, bool initialState)
    {
        throw new NotImplementedException();
    }

    public virtual ValueTask DeserializeAsync(IMessageReader reader, bool initialState)
    {
        return default;
    }
}
