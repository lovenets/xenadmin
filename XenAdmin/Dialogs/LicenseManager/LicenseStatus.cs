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
using System.ComponentModel;
using System.Linq;
using XenAdmin.Core;
using XenAdmin.Utils;
using XenAPI;

namespace XenAdmin.Dialogs
{
    public interface ILicenseStatus : IDisposable
    {
        LicenseStatus.HostState CurrentState { get; }
        Host.Edition LicenseEdition { get; }
        TimeSpan LicenseExpiresIn { get; }
        TimeSpan LicenseExpiresExactlyIn { get; }
        DateTime? ExpiryDate { get; }
        event LicenseStatus.StatusUpdatedEvent ItemUpdated;
        bool Updated { get; }
        void BeginUpdate();
        Host LicencedHost { get; }
        bool IsUsingPerSocketGenerationLicenses { get; }
    }

    public class LicenseStatus : ILicenseStatus
    {
        public enum HostState
        {
            Unknown,
            Expired,
            ExpiresSoon,
            RegularGrace,
            UpgradeGrace,
            Licensed,
            PartiallyLicensed,
            Free,
            Unavailable
        }

        private readonly EventHandlerList events = new EventHandlerList();
        protected EventHandlerList Events { get { return events; } }
        private const string StatusUpdatedEventKey = "LicenseStatusStatusUpdatedEventKey";

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        public Host LicencedHost { get; private set; }
        private readonly AsyncServerTime serverTime = new AsyncServerTime();
        public delegate void StatusUpdatedEvent(object sender, EventArgs e);

        private IXenObject XenObject { get; set; }

        public bool Updated { get; set; }

        public LicenseStatus(IXenObject xo)
        {
            SetDefaultOptions();
            XenObject = xo;

            if (xo is Host)
                LicencedHost = xo as Host;
            if (xo is Pool)
            {
                Pool pool = xo as Pool;
                SetMinimumLicenseValueHost(pool);
            }
            serverTime.ServerTimeObtained += ServerTimeUpdatedEventHandler;
        }

        private void SetMinimumLicenseValueHost(Pool pool)
        {
            LicencedHost = pool.Connection.Resolve(pool.master);

            if(LicencedHost == null)
                return;

            foreach (Host host in pool.Connection.Cache.Hosts)
            {
                if(host.LicenseExpiryUTC < LicencedHost.LicenseExpiryUTC)
                    LicencedHost = host;
            }
        }

        private void SetDefaultOptions()
        {
            CurrentState = HostState.Unknown;
            Updated = false;
            LicenseExpiresExactlyIn = new TimeSpan();
        }

        public void BeginUpdate()
        {
            SetDefaultOptions();
            serverTime.Fetch(LicencedHost);
        }

        private void ServerTimeUpdatedEventHandler(object sender, AsyncServerTimeEventArgs e)
        {
            if (!e.Success)
            {
                if(e.QueriedHost == null)
                {
                    log.ErrorFormat("Couldn't get the server time because: {0}", e.Failure.Message);
                    return;
                }

                log.ErrorFormat("Couldn't get the server time for {0} because: {1}", e.QueriedHost.name_label, e.Failure.Message);
                return;
            }

            if (LicencedHost != null)
            {
                CalculateLicenseState();
                TriggerStatusUpdatedEvent();
            }
        }

        protected void CalculateLicenseState()
        {
            LicenseExpiresExactlyIn = CalculateLicenceExpiresIn();
            CurrentState = CalculateCurrentState();
            Updated = true;
        }

        private void TriggerStatusUpdatedEvent()
        {
            StatusUpdatedEvent handler = Events[StatusUpdatedEventKey] as StatusUpdatedEvent;
            if (handler != null)
                handler.Invoke(this, EventArgs.Empty);
        }

        private bool InRegularGrace
        {
            get
            {
                return LicencedHost.license_params.ContainsKey("grace") && LicenseExpiresIn.Ticks > 0 && LicencedHost.license_params["grace"] == "regular grace";
            }
        }

        private bool InUpgradeGrace
        {
            get
            {
                return LicencedHost.license_params.ContainsKey("grace") && LicenseExpiresIn.Ticks > 0 && LicencedHost.license_params["grace"] == "upgrade grace";
            }
        }

        protected virtual TimeSpan CalculateLicenceExpiresIn()
        {
            DateTime currentRefTime = serverTime.ServerTime;
            return Helpers.LicenceExpiresIn(LicencedHost, currentRefTime);
        }

        public bool IsUsingPerSocketGenerationLicenses
        {
            get { return Helpers.ClearwaterOrGreater(LicencedHost); }
        }

        private bool PoolIsPartiallyLicensed
        {
            get
            {
                if(XenObject is Pool)
                {
                    if(XenObject.Connection.Cache.Hosts.Length == 1)
                        return false;

                    int freeCount = XenObject.Connection.Cache.Hosts.Count(h => Host.GetEdition(h.edition) == Host.Edition.Free);
                    return freeCount > 0 &&  freeCount < XenObject.Connection.Cache.Hosts.Length;
                }
                return false;
            }
        }

        private HostState CalculateCurrentState()
        {

            if (ExpiryDate.HasValue && ExpiryDate.Value.Day == 1 && ExpiryDate.Value.Month == 1 && ExpiryDate.Value.Year == 1970)
            {
                return HostState.Unavailable;
            }

            if (PoolIsPartiallyLicensed)
                return HostState.PartiallyLicensed;

            if (IsUsingPerSocketGenerationLicenses)
            {
                if (LicenseEdition == Host.Edition.Free)
                    return HostState.Expired;
                
                if (LicenseExpiresIn.TotalDays >= 30)
                    return HostState.Licensed;
            }

            if (LicenseExpiresIn.TotalDays > 3653)
            {
                return HostState.Licensed;
            }

            if (LicenseExpiresIn.Ticks <= 0)
            {
                return HostState.Expired;
            }

            if (LicenseExpiresIn.TotalDays < 30)
            {
                if (InRegularGrace)
                    return  HostState.RegularGrace;
                if (InUpgradeGrace)
                    return HostState.UpgradeGrace;

                return HostState.ExpiresSoon;
            }

            return LicenseEdition ==  Host.Edition.Free ? HostState.Free : HostState.Licensed;
        }

        #region ILicenseStatus Members
        public event StatusUpdatedEvent ItemUpdated
        {
            add { Events.AddHandler(StatusUpdatedEventKey, value); }
            remove { Events.RemoveHandler(StatusUpdatedEventKey, value); }
        }

        public Host.Edition LicenseEdition { get { return Host.GetEdition(LicencedHost.edition); } }

        public HostState CurrentState { get; private set; }

        public TimeSpan LicenseExpiresExactlyIn { get; private set; }

        /// <summary>
        /// License expiry, just days, hrs, mins
        /// </summary>
        public TimeSpan LicenseExpiresIn
        { 
            get
            {
                return new TimeSpan(LicenseExpiresExactlyIn.Days, LicenseExpiresExactlyIn.Hours, LicenseExpiresExactlyIn.Minutes, 0, 0);
            }
        }

        public DateTime? ExpiryDate
        {
            get
            {
                if (LicencedHost.license_params.ContainsKey("expiry"))
                    return LicencedHost.LicenseExpiryUTC.ToLocalTime();
                return null;
            }
        }

        #endregion

        #region IDisposable Members
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool disposed;
        public void Dispose(bool disposing)
        {
            if(!disposed)
            {
                if(disposing)
                {
                    Events.Dispose();
                }
                disposed = true;
            }
        }

        #endregion
    }
}
