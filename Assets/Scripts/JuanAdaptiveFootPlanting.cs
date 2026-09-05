using System;
using UnityEngine;
using UnityEngine.U2D.IK;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>Animates the existing Juan rig from root displacement, without moving the root.</summary>
[ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(-15)]
public sealed class JuanAdaptiveFootPlanting : MonoBehaviour
{
#if UNITY_EDITOR
    [UnityEditor.MenuItem("CONTEXT/JuanAdaptiveFootPlanting/Configure Rig")]
    static void ConfigureRigMenu(UnityEditor.MenuCommand cmd)
    {
        ((JuanAdaptiveFootPlanting)cmd.context).ConfigureRig();
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

    [Header("Adaptive foot planting")]
    [Min(0.05f)] public float stepError = 0.45f;
    [Min(0.05f)] public float walkStepDuration = 0.32f;
    [Min(0.05f)] public float runStepDuration = 0.18f;
    [Range(0f, 1f)] public float landingLead = 0.45f;

    [Header("Side standing pose")]
    [Min(0f)] public float standingFootHalfSpread = 0.12f;
    [Min(0f)] public float standingHandHalfSpread = 0.34f;
    [Min(0f)] public float standingHandDrop = 0.22f;
    [Min(0.1f)] public float settleSpeed = 4f;
    [Range(0f, 0.05f)] public float idleBreath = 0.012f;

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
        public bool initialized;
        public bool planted;
        public bool stepping;
        public Vector3 anchor, swingStart, landing;
        public float progress, duration;
    }
    readonly FootState leftFoot = new FootState();
    readonly FootState rightFoot = new FootState();
    Vector3 previousPosition, previousScale;
    Vector2 direction = Vector2.right;
    float speed, runBlend, motionWeight, phase, idleBlend;
    bool ready, posing, stepLeftNext = true;
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
        previousPosition = transform.position;
        previousScale = transform.lossyScale;
        speed = runBlend = motionWeight = phase = idleBlend = 0f;
        ResetFoot(leftFoot);
        ResetFoot(rightFoot);
        stepLeftNext = true;
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
        if ((transform.lossyScale - previousScale).sqrMagnitude > 0.00001f || delta.magnitude > worldLeg * 2f || dt > 0.2f)
        {
            RestorePose();
            ResetTracking();
            return;
        }
        float distance = new Vector2(delta.x, delta.y).magnitude;
        float measuredSpeed = distance / dt;
        float blend = 1f - Mathf.Exp(-response * dt);
        speed = Mathf.Lerp(speed, measuredSpeed, blend);
        if (Mathf.Abs(delta.x) > 0.00001f) direction = new Vector2(Mathf.Sign(delta.x), 0f);
        runBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(runStartSpeed, Mathf.Max(runStartSpeed + 0.01f, fullRunSpeed), speed));
        motionWeight = Mathf.Lerp(motionWeight, measuredSpeed > stopSpeed ? 1f : 0f, blend);
        posing = true;
        RestoreTransforms();
        EnsureFoot(leftLeg, leftFoot);
        EnsureFoot(rightLeg, rightFoot);
        bool moving = measuredSpeed > stopSpeed || speed > stopSpeed;
        if (moving)
        {
            idleBlend = 0f;
            float stride = worldLeg * Mathf.Lerp(walkStride, runStride, runBlend);
            phase = Mathf.Repeat(phase + distance / Mathf.Max(0.01f, stride), 1f);
            UpdateSteps(dt, stride, worldLeg);
            ApplyLocomotionBody();
            ApplyFootTarget(leftLeg, leftFoot);
            ApplyFootTarget(rightLeg, rightFoot);
            PoseArmsFromFeet();
        }
        else
        {
            AdvanceStep(leftFoot, dt);
            AdvanceStep(rightFoot, dt);
            if (leftFoot.stepping || rightFoot.stepping)
            {
                ApplyLocomotionBody();
                ApplyFootTarget(leftLeg, leftFoot);
                ApplyFootTarget(rightLeg, rightFoot);
                PoseArmsFromFeet();
            }
            else ApplyStandingPose(dt);
        }
    }

void ApplyStandingPose(float dt)
    {
        idleBlend = Mathf.MoveTowards(idleBlend, 1f, dt * settleSpeed);
        Vector3 center = (leftFootHome + rightFootHome) * 0.5f;
        float footSpread = legLength * standingFootHalfSpread;
        Vector3 leftStand = transform.TransformPoint(center + Vector3.left * footSpread);
        Vector3 rightStand = transform.TransformPoint(center + Vector3.right * footSpread);
        leftFoot.anchor = Vector3.Lerp(leftFoot.anchor, leftStand, idleBlend);
        rightFoot.anchor = Vector3.Lerp(rightFoot.anchor, rightStand, idleBlend);
        SetReachableTarget(leftLeg, leftFoot.anchor);
        SetReachableTarget(rightLeg, rightFoot.anchor);
        float breath = Mathf.Sin(Time.realtimeSinceStartup * 2f) * legLength * idleBreath;
        hips.localPosition += Vector3.up * breath;
        if (chest) chest.localRotation *= Quaternion.Euler(0f, 0f, breath * 20f);
        PoseStandingArms();
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
    void ResetFoot(FootState state)
    {
        state.initialized = false;
        state.stepping = false;
        state.progress = state.duration = 0f;
    }

    void EnsureFoot(LimbSolver2D solver, FootState state)
    {
        if (state.initialized) return;
        state.initialized = true;
        state.anchor = state.swingStart = state.landing = solver.GetChain(0).target.position;
    }

    void UpdateSteps(float dt, float stride, float worldLeg)
    {
        AdvanceStep(leftFoot, dt);
        AdvanceStep(rightFoot, dt);
        if (leftFoot.stepping || rightFoot.stepping) return;
        FootState foot = stepLeftNext ? leftFoot : rightFoot;
        Vector3 ideal = IdealFootPosition(stepLeftNext, stride);
        if (Mathf.Abs(foot.anchor.x - ideal.x) < worldLeg * stepError) return;
        foot.stepping = true;
        foot.progress = 0f;
        foot.duration = Mathf.Lerp(walkStepDuration, runStepDuration, runBlend);
        foot.swingStart = foot.anchor;
        foot.landing = ideal;
        stepLeftNext = !stepLeftNext;
    }

    Vector3 IdealFootPosition(bool isLeft, float stride)
    {
        Vector3 neutral = (leftFootHome + rightFootHome) * 0.5f;
        neutral.x += isLeft ? -legLength * 0.015f : legLength * 0.015f;
        Vector3 desired = transform.TransformPoint(neutral);
        desired.x += (direction.x >= 0f ? 1f : -1f) * stride * landingLead;
        return desired;
    }

    void AdvanceStep(FootState state, float dt)
    {
        if (!state.stepping) return;
        state.progress = Mathf.Clamp01(state.progress + dt / Mathf.Max(0.01f, state.duration));
        if (state.progress >= 1f)
        {
            state.anchor = state.landing;
            state.stepping = false;
        }
    }

    void ApplyFootTarget(LimbSolver2D solver, FootState state)
    {
        Vector3 desired = state.anchor;
        if (state.stepping)
        {
            float eased = state.progress * state.progress * (3f - 2f * state.progress);
            desired = Vector3.Lerp(state.swingStart, state.landing, eased);
            float lift = transform.TransformVector(Vector3.up * legLength * Mathf.Lerp(walkLift, runLift, runBlend)).magnitude;
            desired += transform.up * (Mathf.Sin(state.progress * Mathf.PI) * lift);
        }
        SetReachableTarget(solver, desired);
    }

    void ApplyLocomotionBody()
    {
        float wave = Mathf.Sin(phase * Mathf.PI * 2f);
        float bounce = -Mathf.Cos(phase * Mathf.PI * 4f);
        Vector3 hipOffset = Vector3.up * legLength * bodyBounce * (bounce - 0.5f) * Mathf.Lerp(1f, 1.7f, runBlend) * motionWeight;
        hips.localPosition += hipOffset + Vector3.right * (wave * legLength * 0.015f * motionWeight);
        float facing = direction.x;
        hips.localRotation *= Quaternion.Euler(0f, 0f, (wave * 2.5f - facing * Mathf.Lerp(2f, runLean, runBlend)) * motionWeight);
        if (chest) chest.localRotation *= Quaternion.Euler(0f, 0f, wave * 2f * motionWeight);
        if (head) head.localRotation *= Quaternion.Euler(0f, 0f, facing * runLean * runBlend * motionWeight * 0.6f);
    }

    void PoseArmsFromFeet()
    {
        float span = Mathf.Max(0.01f, legLength * Mathf.Lerp(walkStride, runStride, runBlend));
        float center = (leftFootHome.x + rightFootHome.x) * 0.5f;
        float leftStride = Mathf.Clamp((transform.InverseTransformPoint(leftFoot.anchor).x - center) / span, -1f, 1f);
        float rightStride = Mathf.Clamp((transform.InverseTransformPoint(rightFoot.anchor).x - center) / span, -1f, 1f);
        PoseHand(leftArm, leftHandHome, -leftStride);
        PoseHand(rightArm, rightHandHome, -rightStride);
    }

    void PoseStandingArms()
    {
        Vector3 center = (leftHandHome + rightHandHome) * 0.5f;
        Vector3 left = center + Vector3.left * (legLength * standingHandHalfSpread) + Vector3.down * (legLength * standingHandDrop);
        Vector3 right = center + Vector3.right * (legLength * standingHandHalfSpread) + Vector3.down * (legLength * standingHandDrop);
        SetReachableTarget(leftArm, transform.TransformPoint(left));
        SetReachableTarget(rightArm, transform.TransformPoint(right));
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
