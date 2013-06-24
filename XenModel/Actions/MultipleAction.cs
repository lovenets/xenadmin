﻿/* Copyright (c) Citrix Systems Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;


namespace XenAdmin.Actions
{
    // Run several actions. The outer action is asynchronous, but the subactions are run synchronously within that.
    public class MultipleAction : AsyncAction, IDisposable
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected readonly List<AsyncAction> subActions;
        private readonly string endDescription;
        public bool ShowSubActionsDetails { get; private set; }
        public string SubActionTitle { get; private set; }
        public string SubActionDescription { get; private set; }

        public MultipleAction(IXenConnection connection, string title, string startDescription, string endDescription, List<AsyncAction> subActions)
            : base(connection, title, startDescription)
        {
            this.endDescription = endDescription;
            this.subActions = subActions;
            this.Completed += MultipleAction_Completed;
        }

        public MultipleAction(IXenConnection connection, string title, string startDescription, string endDescription,
            List<AsyncAction> subActions, bool showSubActionsDetails)
            : this(connection, title, startDescription, endDescription, subActions)
        {
            ShowSubActionsDetails = showSubActionsDetails;
            if (showSubActionsDetails)
                RegisterEvents();
        }

        // The multiple action gets its ApiMethodsToRoleCheck by accumulating the lists from each of the subactions
        public override RbacMethodList GetApiMethodsToRoleCheck
        {
            get
            {
                RbacMethodList list = new RbacMethodList();
                foreach (AsyncAction subAction in subActions)
                    list.AddRange(subAction.GetApiMethodsToRoleCheck);
                return list;
            }
        }
        
        protected override void Run()
        {
            PercentComplete = 0;
            var exceptions = new List<Exception>();
            
            RunSubActions(exceptions);

            PercentComplete = 100;
            Description = endDescription;
            if (exceptions.Count > 0)
            {
                foreach (Exception e in exceptions)
                    log.Error(e);

                Exception = new Exception(Messages.SOME_ERRORS_ENCOUNTERED);
            }
        }

        private void RegisterEvents()
        {
            foreach (AsyncAction subAction in subActions)
                subAction.Changed += SubActionChanged;
        }

        private void DeregisterEvents()
        {
            foreach (AsyncAction subAction in subActions)
                subAction.Changed -= SubActionChanged;
        }

        private void SubActionChanged(object sender, EventArgs e)
        {
            AsyncAction subAction = sender as AsyncAction;
            if (subAction != null)
            {
                SubActionTitle = subAction.Title;
                SubActionDescription = subAction.Description;
                OnChanged();
            }
        }

        protected virtual void RunSubActions(List<Exception> exceptions)
        {
            int i = 0;
            foreach (AsyncAction subAction in subActions)
            {
                try
                {
                    subAction.RunExternal(Session);
                }
                catch (Exception e)
                {
                    Failure f = e as Failure;
                    if (f != null && Connection != null && f.ErrorDescription[0] == Failure.RBAC_PERMISSION_DENIED)
                    {
                        Failure.ParseRBACFailure(f, Connection, Session ?? Connection.Session);
                    }
                    exceptions.Add(e);
                    // Record the first exception we come to. Though later if there are more than one we will replace this with non specific one.
                    if (Exception == null)
                        Exception = e;
                }
                i++;
                PercentComplete = 100 * i / subActions.Count;
            }
        }

        protected virtual void MultipleAction_Completed(object sender, EventArgs e)
        {
            // The MultipleAction can sometimes complete prematurely, for example
            // if the sudo dialog is cancelled, of (less likely) if one of the
            // subactions throws an exception in its GetApiMethodsToRoleCheck.
            // In this case, we need to cancel this action's subactions.
            foreach (AsyncAction subAction in subActions)
            {
                if (!subAction.IsCompleted)
                    subAction.Cancel();
            }
        }

        #region IDisposable Members

        public void Dispose()
        {
            DeregisterEvents();
        }

        #endregion
    }
}