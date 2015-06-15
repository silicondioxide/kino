﻿namespace Console
{
    public class MessageMap
    {
        public MessageHandler Handler { get; set; }
        public MessageDefinition Message { get; set; }
    }

    public class MessageDefinition
    {
        public string Type { get; set; }
    }
}