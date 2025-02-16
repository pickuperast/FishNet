﻿using FishNet.CodeGenerating;
using FishNet.Connection;
using FishNet.Documenting;
using FishNet.Managing;
using FishNet.Managing.Transporting;
using FishNet.Object.Delegating;
using FishNet.Object.Synchronizing;
using FishNet.Object.Synchronizing.Internal;
using FishNet.Serializing;
using FishNet.Serializing.Helping;
using FishNet.Transporting;
using FishNet.Utility.Extension;
using GameKit.Dependencies.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FishNet.Object
{

    public abstract partial class NetworkBehaviour : MonoBehaviour
    {
        #region Types.
        /// <summary>
        /// Used to generate data sent from synctypes.
        /// </summary>
        private class SyncTypeWriter
        {
            /// <summary>
            /// Clients which can be synchronized.
            /// </summary>
            public ReadPermission ReadPermission;
            /// <summary>
            /// Writers for each channel.
            /// </summary>
            public PooledWriter[] Writers { get; private set; }

            public SyncTypeWriter(ReadPermission readPermission)
            {
                ReadPermission = readPermission;
                Writers = new PooledWriter[TransportManager.CHANNEL_COUNT];
                for (int i = 0; i < Writers.Length; i++)
                    Writers[i] = WriterPool.Retrieve();
            }

            /// <summary>
            /// Resets Writers.
            /// </summary>
            public void Reset()
            {
                if (Writers == null)
                    return;

                for (int i = 0; i < Writers.Length; i++)
                    Writers[i].Reset();
            }
        }
        #endregion

        #region Private.
        /// <summary>
        /// Writers for syncTypes. A writer will exist for every ReadPermission type.
        /// </summary>
        private SyncTypeWriter[] _syncTypeWriters;
        /// <summary>
        /// SyncTypes within this NetworkBehaviour.
        /// </summary>
        private Dictionary<uint, SyncBase> _syncTypes = new Dictionary<uint, SyncBase>();
        /// <summary>
        /// True if at least one syncType is dirty.
        /// </summary>
        private bool _syncTypeDirty;
        /// <summary>
        /// All ReadPermission values.
        /// </summary>
        private static ReadPermission[] _readPermissions;
        #endregion

        internal bool SyncTypeDirty => _syncTypeDirty;

        /// <summary>
        /// Registers a SyncType.
        /// </summary>
        /// <param name="sb"></param>
        /// <param name="index"></param>
        internal void RegisterSyncType(SyncBase sb, uint index)
        {
            if (!_syncTypes.TryAdd(index, sb))
                NetworkManager.LogError($"SyncType key {index} has already been added for {GetType().FullName} on {gameObject.name}");
        }
        /// <summary>
        /// Sets a SyncVar as dirty.
        /// </summary>
        /// <param name="isSyncObject">True if dirtying a syncObject.</param>
        /// <returns>True if able to dirty SyncType.</returns>
        internal bool DirtySyncType()
        {
            if (!IsServerStarted)
                return false;
            /* No reason to dirty if there are no observers.
             * This can happen even if a client is going to see
             * this object because the server side initializes
             * before observers are built. */
            if (_networkObjectCache.Observers.Count == 0 && !_networkObjectCache.PredictedSpawner.IsValid)
                return false;

            if (!_syncTypeDirty)
                _networkObjectCache.NetworkManager.ServerManager.Objects.SetDirtySyncType(this);
            _syncTypeDirty = true;

            return true;
        }

        /// <summary>
        /// Initializes SyncTypes. This will only call once even as host.
        /// </summary>
        private void InitializeOnceSyncTypes(bool asServer)
        {
            if (asServer)
            {
                if (!_initializedOnceServer)
                {
                    //optimization Cache synctypewriters on despawn and get from cache on spawn.
                    //Only need to initialize readpermissions once, it's static.
                    if (_readPermissions == null)
                    {
                        System.Array arr = System.Enum.GetValues(typeof(ReadPermission));
                        _readPermissions = new ReadPermission[arr.Length];

                        int count = 0;
                        foreach (ReadPermission rp in arr)
                        {
                            _readPermissions[count] = rp;
                            count++;
                        }
                    }

                    //Build writers for observers and owner.
                    _syncTypeWriters = new SyncTypeWriter[_readPermissions.Length];
                    for (int i = 0; i < _syncTypeWriters.Length; i++)
                        _syncTypeWriters[i] = new SyncTypeWriter(_readPermissions[i]);
                }
                else
                {
                    //Reset writers.
                    for (int i = 0; i < _syncTypeWriters.Length; i++)
                        _syncTypeWriters[i].Reset();
                }
            }

            /* Initialize synctypes every spawn because there could be
             * callbacks which occur that the user or even we may implement
             * during the initialization. */
            foreach (SyncBase sb in _syncTypes.Values)
                sb.PreInitialize(_networkObjectCache.NetworkManager);
        }


        /// <summary>
        /// Reads a SyncVar.
        /// </summary>
        /// <param name="reader"></param>
        internal void OnSyncType(PooledReader reader, int length, bool asServer = false)
        {
            int readerStart = reader.Position;
            while (reader.Position - readerStart < length)
            {
                byte index = reader.ReadByte();
                if (_syncTypes.TryGetValueIL2CPP(index, out SyncBase sb))
                    sb.Read(reader, asServer);
                else
                    NetworkManager.LogWarning($"SyncType not found for index {index} on {transform.name}. Remainder of packet may become corrupt.");
            }
        }

        /// <summary>
        /// Writers dirty SyncTypes if their write tick has been met.
        /// </summary>
        /// <returns>True if there are no pending dirty sync types.</returns>
        internal bool WriteDirtySyncTypes(bool isDespawn = false)
        {
            /* Can occur when a synctype is queued after
             * the object is marked for destruction. This should not
             * happen under most conditions since synctypes will be
             * pushed through when despawn is called. */
            if (!IsSpawned)
            {
                return true;
            }

            /* If there is nothing dirty then return true, indicating no more
             * pending dirty checks. */
            if (!_syncTypeDirty || _syncTypes.Count == 0)
                return true;

            /* True if writers have been reset for this check.
             * For perf writers are only reset when data is to be written. */
            bool writersReset = false;
            uint tick = _networkObjectCache.NetworkManager.TimeManager.Tick;

            //True if a syncvar is found to still be dirty.
            bool dirtyFound = false;
            //True if data has been written and is ready to send.
            bool dataWritten = false;

            foreach (SyncBase sb in _syncTypes.Values)
            {
                if (!sb.IsDirty)
                    continue;

                dirtyFound = true;
                if (isDespawn || sb.SyncTimeMet(tick))
                {
                    //If writers still need to be reset.
                    if (!writersReset)
                    {
                        writersReset = true;
                        //Reset writers.
                        for (int i = 0; i < _syncTypeWriters.Length; i++)
                            _syncTypeWriters[i].Reset();
                    }

                    //Find channel.
                    byte channel = (byte)(isDespawn ? Channel.Reliable : sb.Channel);
                    sb.ResetDirty(forceReset: isDespawn);
                    //If ReadPermission is owner but no owner skip this syncvar write.
                    if (sb.Settings.ReadPermission == ReadPermission.OwnerOnly && !_networkObjectCache.Owner.IsValid)
                        continue;

                    dataWritten = true;
                    //Find PooledWriter to use.
                    PooledWriter writer = null;
                    for (int i = 0; i < _syncTypeWriters.Length; i++)
                    {
                        if (_syncTypeWriters[i].ReadPermission == sb.Settings.ReadPermission)
                        {
                            /* Channel for syncVar is beyond available channels in transport.
                             * Use default reliable. */
                            if (channel >= _syncTypeWriters[i].Writers.Length)
                                channel = (byte)Channel.Reliable;

                            writer = _syncTypeWriters[i].Writers[channel];
                            break;
                        }
                    }

                    if (writer == null)
                        NetworkManager.LogError($"Writer couldn't be found for permissions {sb.Settings.ReadPermission} on channel {channel}.");
                    else
                        sb.WriteDelta(writer);
                }
            }

            //At least one sync type was dirty.
            if (dataWritten)
            {
                for (int i = 0; i < _syncTypeWriters.Length; i++)
                {
                    for (byte channel = 0; channel < _syncTypeWriters[i].Writers.Length; channel++)
                    {
                        PooledWriter channelWriter = _syncTypeWriters[i].Writers[channel];
                        //If there is data to send.
                        if (channelWriter.Length > 0)
                        {
                            PooledWriter headerWriter = WriterPool.Retrieve();
                            //Write the packetId and NB information.
                            headerWriter.WritePacketId(PacketId.SyncType);
                            PooledWriter dataWriter = WriterPool.Retrieve();
                            dataWriter.WriteNetworkBehaviour(this);
                            dataWriter.WriteBytesAndSize(channelWriter.GetBuffer(), 0, channelWriter.Length);
                            //Attach data onto packetWriter.
                            headerWriter.WriteArraySegment(dataWriter.GetArraySegment());
                            dataWriter.Store();


                            //If only sending to owner.
                            if (_syncTypeWriters[i].ReadPermission == ReadPermission.OwnerOnly)
                            {
                                _networkObjectCache.NetworkManager.TransportManager.SendToClient(channel, headerWriter.GetArraySegment(), _networkObjectCache.Owner);
                            }
                            //Sending to observers.
                            else
                            {
                                bool excludeOwner = (_syncTypeWriters[i].ReadPermission == ReadPermission.ExcludeOwner);
                                SetNetworkConnectionCache(false, excludeOwner);
                                _networkObjectCache.NetworkManager.TransportManager.SendToClients((byte)channel, headerWriter.GetArraySegment(), _networkObjectCache.Observers, _networkConnectionCache);

                            }

                            headerWriter.Store();
                        }
                    }
                }
            }

            //If no dirty were found or we flushed all dirty values from a despawn
            if (!dirtyFound || isDespawn)
            {
                _syncTypeDirty = false;
                return true;
            }

            /* If here then sync types are still pending
             * being written or were just written this frame. */
            return false;
        }


        /// <summary>
        /// Resets all SyncTypes for this NetworkBehaviour.
        /// </summary>
        internal void SyncTypes_ResetState()
        {
            foreach (SyncBase item in _syncTypes.Values)
                item.ResetState();

            _syncTypeDirty = false;
        }

        /// <summary>
        /// Resets all SyncVar fields for the class to the values within their SyncVar class.
        /// EG: _mySyncVar = generated_mySyncVar.GetValue(...)
        /// </summary>
        [MakePublic]
        internal virtual void ResetSyncVarFields() { }

        /// <summary>
        /// Writers syncVars for a spawn message.
        /// </summary>
        /// <param name="conn">Connection SyncTypes are being written for.</param>
        internal void WriteSyncTypesForSpawn(PooledWriter writer, NetworkConnection conn)
        {
            PooledWriter syncTypeWriter = WriterPool.Retrieve();
            /* Since all values are being written everything is
             * written in order so there's no reason to pass
             * indexes. */
            foreach (SyncBase sb in _syncTypes.Values)
            {
                /* If connection is null then write for all.
                 * This can only occur when client is sending syncTypes
                 * to the server. This will be removed when predicted
                 * spawning payload is added in. */ //todo remove this after predicted spawning payload.
                if (conn != null)
                {
                    //True if conn is the owner of this object.
                    bool connIsOwner = (conn == _networkObjectCache.Owner);
                    //Read permissions for the synctype.
                    ReadPermission rp = sb.Settings.ReadPermission;
                    /* SyncType only allows owner to receive values and
                     * conn is not the owner. */
                    if (rp == ReadPermission.OwnerOnly && !connIsOwner)
                        continue;
                    //Write to everyone but the owner.
                    if (rp == ReadPermission.ExcludeOwner && connIsOwner)
                        continue;
                }

                //Anything beyond this is fine to write for everyone.
                sb.WriteFull(syncTypeWriter);
            }

            writer.WriteBytesAndSize(syncTypeWriter.GetBuffer(), 0, syncTypeWriter.Length);
            syncTypeWriter.Store();
        }

    }


}

