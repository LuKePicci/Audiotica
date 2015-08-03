﻿//*********************************************************
//
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
//*********************************************************

using System;
using System.Runtime.Serialization;

namespace Audiotica.Core.Windows.Messages
{
    public class AppSuspendedMessage
    {
        public AppSuspendedMessage()
        {
            this.Timestamp = DateTime.Now;
        }

        public AppSuspendedMessage(DateTime timestamp)
        {
            this.Timestamp = timestamp;
        }
        
        public DateTime Timestamp;
    }
}
