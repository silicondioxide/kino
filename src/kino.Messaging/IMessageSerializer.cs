﻿namespace kino.Messaging
{
    public interface IMessageSerializer
    {
        byte[] Serialize(object obj);

        T Deserialize<T>(byte[] buffer);
    }
}