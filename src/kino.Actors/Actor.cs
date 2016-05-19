﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using kino.Core.Connectivity;

namespace kino.Actors
{
    public abstract class Actor : IActor
    {
        public virtual IEnumerable<MessageHandlerDefinition> GetInterfaceDefinition()
        {
            return GetActorRegistrationsByAttributes();
        }

        private IEnumerable<MessageHandlerDefinition> GetActorRegistrationsByAttributes()
        {
            var methods = this.GetType()
                              .FindMembers(MemberTypes.Method,
                                           BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                           InterfaceMethodFilter,
                                           typeof (MessageHandlerDefinitionAttribute))
                              .Cast<MethodInfo>();

            return methods.Select(Selector);
        }

        private MessageHandlerDefinition Selector(MethodInfo method)
        {
            var @delegate = (MessageHandler) Delegate.CreateDelegate(typeof (MessageHandler), this, method);
            var attr = method.GetCustomAttribute<MessageHandlerDefinitionAttribute>();
            var messageIdentifier = MessageIdentifier.Create(attr.MessageType);

            return new MessageHandlerDefinition
                   {
                       Message = new MessageDefinition(messageIdentifier.Identity, messageIdentifier.Version, messageIdentifier.Partition),
                       Handler = @delegate,
                       KeepRegistrationLocal = attr.KeepRegistrationLocal
                   };
        }

        private static bool InterfaceMethodFilter(MemberInfo memberInfo, object filterCriteria)
        {
            return memberInfo.GetCustomAttributes((Type) filterCriteria).Any();
        }
    }
}