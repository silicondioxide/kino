﻿namespace kino.Core.Messaging
{
    public class CallbackKey
    {
        public CallbackKey(long value)
        {
            Value = value;
        }

        public long Value { get; }
    }
}