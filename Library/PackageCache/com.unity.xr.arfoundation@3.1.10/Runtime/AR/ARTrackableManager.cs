using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.XR.ARSubsystems;

#if !UNITY_2019_2_OR_NEWER
using UnityEngine.Experimental;
#endif

namespace UnityEngine.XR.ARFoundation
{
    /// <summary>
    /// A generic manager for components generated by features detected in the physical environment.
    /// </summary>
    /// <remarks>
    /// When the manager is informed that a trackable has been added, a new <c>GameObject</c>
    /// is created with an <c>ARTrackable</c> component on it. If
    /// <see cref="ARTrackableManager{TSubsystem, TSubsystemDescriptor, TSessionRelativeData, TTrackable}.prefab"/>
    /// is not null, then that prefab will be instantiated.
    /// </remarks>
    /// <typeparam name="TSubsystem">The <c>Subsystem</c> which provides this manager data.</typeparam>
    /// <typeparam name="TSubsystemDescriptor">The <c>SubsystemDescriptor</c> required to create the Subsystem.</typeparam>
    /// <typeparam name="TSessionRelativeData">A concrete struct used to hold data provided by the Subsystem.</typeparam>
    /// <typeparam name="TTrackable">The type of component this component will manage (i.e., create, update, and destroy).</typeparam>
    [RequireComponent(typeof(ARSessionOrigin))]
    [DisallowMultipleComponent]
    public abstract class ARTrackableManager<TSubsystem, TSubsystemDescriptor, TSessionRelativeData, TTrackable>
    : SubsystemLifecycleManager<TSubsystem, TSubsystemDescriptor>
        where TSubsystem : TrackingSubsystem<TSessionRelativeData, TSubsystemDescriptor>
        where TSubsystemDescriptor : SubsystemDescriptor<TSubsystem>
        where TSessionRelativeData : struct, ITrackable
        where TTrackable : ARTrackable<TSessionRelativeData, TTrackable>
    {
        /// <summary>
        /// A collection of all trackables managed by this component.
        /// </summary>
        public TrackableCollection<TTrackable> trackables
        {
            get
            {
                return new TrackableCollection<TTrackable>(m_Trackables);
            }
        }

        /// <summary>
        /// Iterates over every instantiated <see cref="ARTrackable"/> and
        /// activates or deactivates its <c>GameObject</c> based on the value of
        /// <paramref name="active"/>.
        /// This calls
        /// <a href="https://docs.unity3d.com/ScriptReference/GameObject.SetActive.html">GameObject.SetActive</a>
        /// on each trackable's <c>GameObject</c>.
        /// </summary>
        /// <param name="active">If <c>true</c> each trackable's <c>GameObject</c> is activated.
        /// Otherwise, it is deactivated.</param>
        public void SetTrackablesActive(bool active)
        {
            foreach (var trackable in trackables)
            {
                trackable.gameObject.SetActive(active);
            }
        }

        /// <summary>
        /// The <c>ARSessionOrigin</c> which will be used to instantiate detected trackables.
        /// </summary>
        protected ARSessionOrigin sessionOrigin { get; private set; }

        /// <summary>
        /// The name prefix that should be used when instantiating new <c>GameObject</c>s.
        /// </summary>
        protected abstract string gameObjectName { get; }

        /// <summary>
        /// The prefab that should be instantiated when adding a trackable.
        /// </summary>
        protected virtual GameObject GetPrefab()
        {
            return null;
        }

        /// <summary>
        /// A dictionary of all trackables, keyed by <c>TrackableId</c>.
        /// </summary>
        protected Dictionary<TrackableId, TTrackable> m_Trackables = new Dictionary<TrackableId, TTrackable>();

        /// <summary>
        /// A dictionary of trackables added via <see cref="CreateTrackableImmediate(TSessionRelativeData)"/> but not yet reported as added.
        /// </summary>
        protected Dictionary<TrackableId, TTrackable> m_PendingAdds = new Dictionary<TrackableId, TTrackable>();

        /// <summary>
        /// Invoked by Unity once when this component wakes up.
        /// </summary>
        protected virtual void Awake()
        {
            sessionOrigin = GetComponent<ARSessionOrigin>();
        }

        /// <summary>
        /// Update is called once per frame. This component's internal state
        /// is first updated, and then the <see cref="trackablesChanged"/> event is invoked.
        /// </summary>
        protected virtual void Update()
        {
            if (subsystem == null)
                return;

            using (var changes = subsystem.GetChanges(Allocator.Temp))
            {
                ClearAndSetCapacity(s_Added, changes.added.Length);
                foreach (var added in changes.added)
                    s_Added.Add(CreateOrUpdateTrackable(added));

                ClearAndSetCapacity(s_Updated, changes.updated.Length);
                foreach (var updated in changes.updated)
                    s_Updated.Add(CreateOrUpdateTrackable(updated));

                ClearAndSetCapacity(s_Removed, changes.removed.Length);
                foreach (var trackableId in changes.removed)
                {
                    TTrackable trackable;
                    if (m_Trackables.TryGetValue(trackableId, out trackable))
                    {
                        m_Trackables.Remove(trackableId);
                        s_Removed.Add(trackable);
                    }
                }
            }

            try
            {
                // User events
                if ((s_Added.Count) > 0 ||
                    (s_Updated.Count) > 0 ||
                    (s_Removed.Count) > 0)
                {
                    OnTrackablesChanged(s_Added, s_Updated, s_Removed);
                }
            }
            finally
            {
                // Make sure destroy happens even if a user callback throws an exception
                foreach (var removed in s_Removed)
                    DestroyTrackable(removed);
            }
        }

        /// <summary>
        /// Invoked when trackables have changed, i.e., added, updated, or removed.
        /// Use this to perform additional logic, or to invoke public events
        /// related to your trackables.
        /// </summary>
        /// <param name="added">A list of trackables added this frame.</param>
        /// <param name="updated">A list of trackables updated this frame.</param>
        /// <param name="removed">A list of trackables removed this frame.
        /// The trackable components are not destroyed until after this method returns.</param>
        protected virtual void OnTrackablesChanged(
            List<TTrackable> added,
            List<TTrackable> updated,
            List<TTrackable> removed)
        { }

        /// <summary>
        /// Invoked after creating the trackable. The trackable's <c>sessionRelativeData</c> property will already be set.
        /// </summary>
        /// <param name="trackable">The newly created trackable.</param>
        protected virtual void OnCreateTrackable(TTrackable trackable)
        { }

        /// <summary>
        /// Invoked just after session relative data has been set on a trackable.
        /// </summary>
        /// <param name="trackable">The trackable that has just been updated.</param>
        /// <param name="sessionRelativeData">The session relative data used to update the trackable.</param>
        protected virtual void OnAfterSetSessionRelativeData(
            TTrackable trackable,
            TSessionRelativeData sessionRelativeData)
        { }

        /// <summary>
        /// Creates a <see cref="TTrackable"/> immediately, leaving it in a "pending" state.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trackables are usually created, updated, or destroyed during <see cref="Update()"/>.
        /// This method creates a trackable immediately, and marks it as "pending"
        /// until it is reported as added by the subsystem. This is useful for subsystems that deal
        /// with trackables that can be both detected and manually created.
        /// </para></<para>
        /// This method does not invoke <see cref="OnTrackablesChanged(List{TTrackable}, List{TTrackable}, List{TTrackable})"/>,
        /// so no "added" notifications will occur until the next call to <see cref="Update"/>.
        /// However, this method does invoke <see cref="ARTrackable{TSessionRelativeData, TTrackable}.updated"/>
        /// on the new trackable.
        /// </para><para>
        /// The trackable will appear in the <see cref="trackables"/> collection immediately.
        /// </para>
        /// </remarks>
        /// <returns>A new <c>TTrackable</c></returns>
        protected TTrackable CreateTrackableImmediate(TSessionRelativeData sessionRelativeData)
        {
            var trackable = CreateOrUpdateTrackable(sessionRelativeData);
            trackable.pending = true;
            m_PendingAdds.Add(trackable.trackableId, trackable);
            return trackable;
        }

        /// <summary>
        /// If in a "pending" state, the trackable with <paramref name="trackableId"/>'s
        /// <see cref="ARTrackable{TSessionRelativeData, TTrackable}.removed"/>
        /// event is invoked, and the trackable's <c>GameObject</c> destroyed if
        /// <see cref="ARTrackable{TSessionRelativeData, TTrackable}.destroyOnRemoval"/> is true.
        /// Otherwise, this method has no effect.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trackables are usually removed only when the subsystem reports they
        /// have been removed during <see cref="Update"/>
        /// </para><para>
        /// This method will immediately remove a trackable only if it was created by
        /// <see cref="CreateTrackableImmediate(TSessionRelativeData)"/>
        /// and has not yet been reported as added by the
        /// <see cref="SubsystemLifecycleManager{TSubsystem, TSubsystemDescriptor}.subsystem"/>.
        /// </para><para>
        /// This can happen if the trackable is created and removed within the same frame, as the subsystem may never
        /// have a chance to report its existence. Derived classes should use this
        /// if they support the concept of manual addition and removal of trackables, as there may not
        /// be a removal event if the trackable is added and removed quickly.
        /// </para><para>
        /// If the trackable is not in a pending state, i.e., it has already been reported as "added",
        /// then this method does nothing.
        /// </para>
        /// <para>
        /// This method does not invoke <see cref="OnTrackablesChanged(List{TTrackable}, List{TTrackable}, List{TTrackable})"/>,
        /// so no "removed" notifications will occur until the next call to <see cref="Update"/> (and only if it was
        /// previously reported as "added").
        /// However, this method does invoke <see cref="ARTrackable{TSessionRelativeData, TTrackable}.removed"/>
        /// on the trackable.
        /// </para>
        /// </remarks>
        /// <returns><c>True</c> if the trackable is "pending" (i.e., not yet reported as "added").</returns>
        protected bool DestroyPendingTrackable(TrackableId trackableId)
        {
            TTrackable trackable;
            if (m_PendingAdds.TryGetValue(trackableId, out trackable))
            {
                m_PendingAdds.Remove(trackableId);
                m_Trackables.Remove(trackableId);
                DestroyTrackable(trackable);
                return true;
            }

            return false;
        }

        static void ClearAndSetCapacity(List<TTrackable> list, int capacity)
        {
            list.Clear();
            if (list.Capacity < capacity)
                list.Capacity = capacity;
        }

        string GetTrackableName(TrackableId trackableId)
        {
            return gameObjectName + " " + trackableId.ToString();
        }

        GameObject CreateGameObject()
        {
            var prefab = GetPrefab();
            if (prefab == null)
            {
                var go = new GameObject();
                go.transform.parent = sessionOrigin.trackablesParent;
                return go;
            }

            return Instantiate(prefab, sessionOrigin.trackablesParent);
        }

        GameObject CreateGameObject(string name)
        {
            var go = CreateGameObject();
            go.name = name;
            return go;
        }

        GameObject CreateGameObject(TrackableId trackableId)
        {
            return CreateGameObject(GetTrackableName(trackableId));
        }

        TTrackable CreateTrackable(TrackableId trackableId)
        {
            var go = CreateGameObject(trackableId);
            var trackable = go.GetComponent<TTrackable>();
            if (trackable == null)
                trackable = go.AddComponent<TTrackable>();

            return trackable;
        }

        TTrackable CreateOrUpdateTrackable(TSessionRelativeData sessionRelativeData)
        {
            var trackableId = sessionRelativeData.trackableId;
            TTrackable trackable;
            if (m_Trackables.TryGetValue(trackableId, out trackable))
            {
                m_PendingAdds.Remove(trackableId);
                trackable.pending = false;
                trackable.SetSessionRelativeData(sessionRelativeData);
            }
            else
            {
                trackable = CreateTrackable(trackableId);
                m_Trackables.Add(trackableId, trackable);
                trackable.SetSessionRelativeData(sessionRelativeData);
                OnCreateTrackable(trackable);
            }

            OnAfterSetSessionRelativeData(trackable, sessionRelativeData);
            trackable.OnAfterSetSessionRelativeData();
            return trackable;
        }

        void DestroyTrackable(TTrackable trackable)
        {
            if (trackable.destroyOnRemoval)
                Destroy(trackable.gameObject);
        }

        static List<TTrackable> s_Added = new List<TTrackable>();

        static List<TTrackable> s_Updated = new List<TTrackable>();

        static List<TTrackable> s_Removed = new List<TTrackable>();
    }
}
