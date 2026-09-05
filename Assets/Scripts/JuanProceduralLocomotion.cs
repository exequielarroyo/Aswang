using System;
using UnityEngine;
using UnityEngine.U2D.IK;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>Animates the existing Juan rig from root displacement, without moving the root.</summary>
[ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(-15)]
public sealed class JuanProceduralLocomotion : MonoBehaviour
{
#if UNITY_EDITOR
    [UnityEditor.MenuItem("CONTEXT/JuanProceduralLocomotion/Configure Rig")]
    static void ConfigureRigMenu(UnityEditor.MenuCommand cmd)
    {
        ((JuanProceduralLocomotion)cmd.context).ConfigureRig();
        UnityEditor.EditorUtility.SetDirty(cmd.context);
    }
#endif
    [Header("Movement response")]
    [Tooltip("Animate when the character is dragged in the Scene view.")]
    public bool previewInScene = true;
    [Min(0.01f), Tooltip("World units per second at which the gait starts becoming a run.")]
    public float runStartSpeed = 2.5f;
    [Min(0.02f)] public float fullRunSpeed = 5f;
    [Min(0f)] public float stopSpeed = 0.035f;
    [Min(1f)] public float response = 10f;
    [Header("Gait (relative to leg length)")]
    [Range(0.1f, 1f)] public float walkStride = 0.48f;
    [Range(0.2f, 1.5f)] public float runStride = 0.9f;
    [Range(0.01f, 0.4f)] public float walkLift = 0.09f;
    [Range(0.02f, 0.6f)] public float runLift = 0.24f;
    [Range(0f, 0.15f)] public float bodyBounce = 0.035f;
    [Range(0f, 20f)] public float runLean = 7f;
    [Range(0f, 0.5f)] public float armSwing = 0.2f;

    [SerializeField, HideInInspector] IKManager2D manager;
    [SerializeField, HideInInspector] Transform hips;
    [SerializeField, HideInInspector] Transform chest;
    [SerializeField, HideInInspector] Transform head;
    [SerializeField, HideInInspector] LimbSolver2D leftLeg, rightLeg, leftArm, rightArm;
    [SerializeField, HideInInspector] Transform[] restTransforms;
    [SerializeField, HideInInspector] Vector3[] restPositions;
    [SerializeField, HideInInspector] Quaternion[] restRotations;
    [SerializeField, HideInInspector] Vector3 leftFootHome, rightFootHome, leftHandHome, rightHandHome;
    [SerializeField, HideInInspector] float legLength;

    sealed class FootState
    {
        public bool initialized, planted;
        public Vector3 anchor, swingStart;
    }
    readonly FootState leftFoot = new FootState();
    readonly FootState rightFoot = new FootState();
    Vector3 previousPosition, previousScale;
    Vector2 direction = Vector2.right;
    float speed, runBlend, motionWeight, phase;
    bool ready, posing;
#if UNITY_EDITOR
    double editorTime;
#endif
    public float CurrentSpeed => speed;
    public float RunBlend => runBlend;
    public bool IsConfigured => manager && hips && leftLeg && rightLeg && leftArm && rightArm
        && restTransforms != null && restTransforms.Length > 0;

    // Explicit setup keeps the authored pose stable across reloads and prefab instances.
    public void ConfigureRig()
    {
        manager = GetComponent<IKManager2D>();
        hips = Find("body_1"); chest = Find("chest"); head = Find("head_1");
        foreach (var solver in GetComponentsInChildren<LimbSolver2D>(true))
        {
            string n = solver.name.ToLowerInvariant();
            if (n == "leg left solver") leftLeg = solver;
            else if (n == "left right solver") rightLeg = solver;
            else if (n == "forearm left solver") leftArm = solver;
            else if (n == "forearm right solver") rightArm = solver;
        }
        if (!manager || !hips || !leftLeg || !rightLeg || !leftArm || !rightArm)
            throw new InvalidOperationException("Juan requires the four existing limb solvers and body bone.");
        foreach (var s in new[] {leftLeg, rightLeg, leftArm, rightArm})
        {
            if (!s.isValid) s.Initialize();
            if (!s.isValid || !s.GetChain(0).target)
                throw new InvalidOperationException("Juan has an invalid IK chain: " + s.name);
        }
        restTransforms = GetComponentsInChildren<Transform>(true);
        restPositions = new Vector3[restTransforms.Length];
        restRotations = new Quaternion[restTransforms.Length];
        for (int i = 0; i < restTransforms.Length; i++)
        {
            restPositions[i] = restTransforms[i].localPosition;
            restRotations[i] = restTransforms[i].localRotation;
        }
        leftFootHome = LocalTarget(leftLeg); rightFootHome = LocalTarget(rightLeg);
        leftHandHome = LocalTarget(leftArm); rightHandHome = LocalTarget(rightArm);
        var chain = leftLeg.GetChain(0);
        legLength = Vector3.Distance(transform.InverseTransformPoint(chain.transforms[0].position),
            transform.InverseTransformPoint(chain.transforms[1].position))
            + Vector3.Distance(transform.InverseTransformPoint(chain.transforms[1].position),
                transform.InverseTransformPoint(chain.effector.position));
        ResetTracking();
    }

    Transform Find(string name)
    {
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }
    Vector3 LocalTarget(LimbSolver2D s) => transform.InverseTransformPoint(s.GetChain(0).target.position);

    void OnEnable()
    {
        if (!IsConfigured)
        {
            try
            {
                ConfigureRig();
#if UNITY_EDITOR
                if (!Application.IsPlaying(gameObject))
                    UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            catch (System.Exception e) { UnityEngine.Debug.LogError(e); }
        }
        ResetTracking();
#if UNITY_EDITOR
        editorTime = EditorApplication.timeSinceStartup;
        EditorApplication.update += EditorTick;
        EditorSceneManager.sceneSaving += BeforeSceneSave;
        AssemblyReloadEvents.beforeAssemblyReload += RestorePose;
#endif
    }
    void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
        EditorSceneManager.sceneSaving -= BeforeSceneSave;
        AssemblyReloadEvents.beforeAssemblyReload -= RestorePose;
#endif
        RestorePose();
    }
    void LateUpdate()
    {
        if (Application.IsPlaying(gameObject)) Tick(Time.deltaTime);
    }
#if UNITY_EDITOR
    void BeforeSceneSave(UnityEngine.SceneManagement.Scene scene, string path)
    {
        if (scene == gameObject.scene) { RestorePose(); ResetTracking(); }
    }
    void EditorTick()
    {
        double now = EditorApplication.timeSinceStartup;
        float dt = (float)(now - editorTime);
        editorTime = now;
        if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode
            || !this || !isActiveAndEnabled || EditorUtility.IsPersistent(this)
            || PrefabStageUtility.GetPrefabStage(gameObject) != null) return;
        if (!previewInScene)
        {
            if (posing) RestorePose();
            ResetTracking();
            return;
        }
        if (dt <= 0f || dt > 0.2f) { RestorePose(); ResetTracking(); return; }
        Tick(dt);
        if (posing)
        {
            manager.UpdateManager();
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
#endif
public void ResetTracking()
    {
        previousPosition = transform.position; previousScale = transform.lossyScale;
        speed = runBlend = motionWeight = phase = 0f;
        leftFoot.initialized = rightFoot.initialized = false;
        ready = true;
    }

    /// <summary>One simulation step; also usable for deterministic gait verification.</summary>
public void Tick(float dt)
    {
        if (!IsConfigured || dt <= 0f) return;
        if (!ready) ResetTracking();
        Vector3 delta = transform.position - previousPosition;
        previousPosition = transform.position;
        float worldLeg = Mathf.Max(0.01f, transform.TransformVector(Vector3.up * legLength).magnitude);
        if ((transform.lossyScale - previousScale).sqrMagnitude > 0.00001f
            || delta.magnitude > worldLeg * 2f || dt > 0.2f)
        {
            RestorePose(); ResetTracking(); return;
        }
        float distance = new Vector2(delta.x, delta.y).magnitude;
        float measuredSpeed = distance / dt;
        float blend = 1f - Mathf.Exp(-response * dt);
        speed = Mathf.Lerp(speed, measuredSpeed, blend);
        if (distance > 0.00001f) direction = new Vector2(delta.x, delta.y).normalized;
        runBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(runStartSpeed,
            Mathf.Max(runStartSpeed + 0.01f, fullRunSpeed), speed));
        motionWeight = Mathf.Lerp(motionWeight, measuredSpeed > stopSpeed ? 1f : 0f, blend);
        if (motionWeight < 0.002f && measuredSpeed <= stopSpeed)
        {
            ApplyStandingPose();
            leftFoot.initialized = rightFoot.initialized = false;
            return;
        }

        posing = true;
        RestoreTransforms();
        float stride = worldLeg * Mathf.Lerp(walkStride, runStride, runBlend);
        float stance = Mathf.Lerp(0.62f, 0.38f, runBlend);
        float advance = distance * stance / Mathf.Max(0.01f, stride);
        phase = Mathf.Repeat(phase + advance, 1f);
        float wave = Mathf.Sin(phase * Mathf.PI * 2f);
        float bounce = -Mathf.Cos(phase * Mathf.PI * 4f);
        Vector3 hipOffset = Vector3.up * legLength * bodyBounce
            * (bounce - 0.5f) * Mathf.Lerp(1f, 1.7f, runBlend) * motionWeight;
        hips.localPosition += hipOffset + Vector3.right * (wave * legLength * 0.015f * motionWeight);
        float facing = direction.x;
        hips.localRotation *= Quaternion.Euler(0, 0, (wave * 2.5f - facing * Mathf.Lerp(2f, runLean, runBlend)) * motionWeight);
        if (chest) chest.localRotation *= Quaternion.Euler(0, 0, wave * 2f * motionWeight);
        if (head) head.localRotation *= Quaternion.Euler(0, 0, facing * runLean * runBlend * motionWeight * 0.6f);

        float lift = worldLeg * Mathf.Lerp(walkLift, runLift, runBlend);
        PoseFoot(leftLeg, leftFoot, leftFootHome, phase, stance, stride, lift);
        PoseFoot(rightLeg, rightFoot, rightFootHome, Mathf.Repeat(phase + 0.5f, 1f), stance, stride, lift);
        float halfStrideLocal = Mathf.Max(0.01f, legLength * Mathf.Lerp(walkStride, runStride, runBlend) * 0.5f);
        float centerX = (leftFootHome.x + rightFootHome.x) * 0.5f;
        float leftStride = Mathf.Clamp((LocalTarget(leftLeg).x - centerX) / halfStrideLocal, -1f, 1f);
        float rightStride = Mathf.Clamp((LocalTarget(rightLeg).x - centerX) / halfStrideLocal, -1f, 1f);
        PoseHand(leftArm, leftHandHome, -leftStride);
        PoseHand(rightArm, rightHandHome, -rightStride);
    }

void ApplyStandingPose()
    {
        posing = true;
        RestoreTransforms();
    }

void PoseFoot(LimbSolver2D solver, FootState state, Vector3 homeLocal,
        float p, float stance, float stride, float lift)
    {
        // Shared neutral station lets both legs pass beneath the pelvis.
        Vector3 neutral = (leftFootHome + rightFootHome) * 0.5f;
        neutral.x += (solver == leftLeg ? -1f : 1f) * legLength * 0.015f;
        neutral.z = homeLocal.z;
        Vector3 home = transform.TransformPoint(neutral);
        Vector3 idleHome = transform.TransformPoint(homeLocal);
        float side = Mathf.Abs(direction.x) > 0.1f ? Mathf.Sign(direction.x) : -1f;
        Vector3 travel = Vector3.right * side;
        bool planted = p < stance;
        if (!state.initialized)
        {
            state.initialized = true; state.planted = planted;
            state.anchor = home + travel * stride * (0.5f - Mathf.Min(p / stance, 1f));
            state.swingStart = home - travel * stride * 0.5f;
        }
        if (planted && !state.planted) state.anchor = home + travel * stride * 0.5f;
        if (!planted && state.planted) state.swingStart = state.anchor;
        state.planted = planted;
        Vector3 target = state.anchor;
        // Root Y is map travel, while foot lift belongs to the visual rig.
        if (planted)
        {
            target = home + travel * stride * (0.5f - p / stance);
            state.anchor = target;
        }
        if (!planted)
        {
            float t = Mathf.InverseLerp(stance, 1f, p);
            float eased = t * t * (3f - 2f * t);
            Vector3 start = state.swingStart;
            start.y = home.y;
            target = Vector3.Lerp(start, home + travel * stride * 0.5f, eased)
                + transform.up * (Mathf.Sin(t * Mathf.PI) * lift);
        }
        SetReachableTarget(solver, Vector3.Lerp(idleHome, target, motionWeight));
    }
void PoseHand(LimbSolver2D solver, Vector3 home, float swing)
    {
        // Center arm arcs beneath the torso, then counter-swing against the legs.
        Vector3 neutral = (leftHandHome + rightHandHome) * 0.5f;
        neutral.z = home.z;
        Vector3 offset = Vector3.right * (swing * legLength * armSwing * Mathf.Lerp(0.9f, 1.4f, runBlend));
        offset.y = Mathf.Abs(swing) * legLength * 0.05f + legLength * 0.12f * runBlend;
        SetReachableTarget(solver, transform.TransformPoint(Vector3.Lerp(home, neutral + offset, motionWeight)));
    }
    void SetReachableTarget(LimbSolver2D solver, Vector3 desired)
    {
        var c = solver.GetChain(0);
        Vector3 origin = c.transforms[0].position;
        float reach = Vector3.Distance(origin, c.transforms[1].position)
            + Vector3.Distance(c.transforms[1].position, c.effector.position);
        Vector3 offset = desired - origin;
        desired = origin + Vector3.ClampMagnitude(offset, reach * 0.985f);
        c.target.position = desired;
    }
    void RestoreTransforms()
    {
        if (restTransforms == null) return;
        for (int i = 0; i < restTransforms.Length; i++)
        {
            var t = restTransforms[i];
            // Never restore the character's root: it belongs to movement/the scene tool.
            if (!t || t == transform) continue;
            t.localPosition = restPositions[i];
            t.localRotation = restRotations[i];
        }
    }
    public void RestorePose()
    {
        if (posing) RestoreTransforms();
        posing = false;
    }
}
