using System;
using UnityEngine;
using UnityEngine.U2D.IK;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Legs-only, world-anchored foot planting for Juan's existing 2D IK rig.
/// It observes horizontal root movement but never drives the root or upper body.
/// </summary>
[ExecuteAlways, DisallowMultipleComponent, DefaultExecutionOrder(-15)]
public sealed class JuanFootPlanting : MonoBehaviour
{
    [Header("Preview and movement")]
    public bool previewInScene = true;
    [Min(0f)] public float stopSpeed = 0.035f;
    [Min(1f)] public float speedResponse = 10f;
    [Min(0.02f)] public float runStartSpeed = 2.5f;
    [Min(0.03f)] public float fullRunSpeed = 5f;
    [Header("Foot placement (relative to leg length)")]
    [Range(0.05f, 0.8f)] public float stepError = 0.2f;
    [Range(0f, 1f)] public float landingLead = 0.5f;
    [Range(0f, 0.1f), Tooltip("Small separation between the two feet while they share the side-walk track.")]
    public float walkTrackOffset = 0.02f;
    [Range(0.05f, 0.35f)] public float standingFootHalfSpread = 0.16f;
    [Range(-30f, 30f), Tooltip("World Z angle used by both foot targets while standing.")]
    public float idleFootAngle = 0f;
    [Range(0.1f, 1.5f)] public float walkStride = 0.5f;
    [Range(0.1f, 1.5f)] public float runStride = 0.85f;
    [Min(0.05f)] public float walkStepDuration = 0.32f;
    [Min(0.05f)] public float runStepDuration = 0.18f;
    [Range(0.01f, 0.6f)] public float walkLift = 0.09f;
    [Range(0.01f, 0.6f)] public float runLift = 0.22f;
    [Header("Standing arms")]
    [Range(0f, 0.6f), Tooltip("Outward hand offset, relative to arm length, while idle.")]
    public float idleArmSideOffset = 0.26f;
    [Range(0.4f, 1f), Tooltip("Vertical hand drop, relative to arm length, while idle.")]
    public float idleArmDrop = 0.82f;

    [Header("Standing and pelvis")]
[Header("Standing and pelvis")]
    [Min(0.01f)] public float settleSpeed = 4f;
    [Min(0f)] public float maxPelvisDrop = 0.08f;
    [Min(1f)] public float pelvisResponse = 12f;
    [Range(0.8f, 1f)] public float comfortableReach = 0.94f;

    [SerializeField, HideInInspector] IKManager2D manager;
    [SerializeField, HideInInspector] Transform hips;
    [SerializeField, HideInInspector] LimbSolver2D leftLeg, rightLeg, leftArm, rightArm;
    [SerializeField, HideInInspector] Vector3 leftFootHomeLocal, rightFootHomeLocal, leftLegRootRestLocal, rightLegRootRestLocal, hipsRestLocalPosition;
    [SerializeField, HideInInspector] Quaternion hipsRestLocalRotation;
    [SerializeField, HideInInspector] float legLengthLocal;

    sealed class FootState
    {
        public bool initialized, stepping;
        public Vector3 plantedWorld, swingStartWorld, landingWorld;
        public float progress, duration;
    }

    readonly FootState leftFoot = new FootState();
    readonly FootState rightFoot = new FootState();
    Vector3 previousRootPosition, previousScale;
    float speed, runBlend, pelvisDrop, idleBlend, travelSinceStep, directionSign = 1f;
    int lastStepped = -1;
    bool ready;
#if UNITY_EDITOR
    double editorTime;
#endif
    public float CurrentSpeed => speed;
    public float RunBlend => runBlend;
    public bool IsConfigured => manager && hips && leftLeg && rightLeg && leftArm && rightArm && legLengthLocal > 0f && leftLegRootRestLocal.sqrMagnitude > 0.0001f && rightLegRootRestLocal.sqrMagnitude > 0.0001f;

#if UNITY_EDITOR
    [MenuItem("CONTEXT/JuanFootPlanting/Configure Rig")]
    static void ConfigureRigMenu(MenuCommand command)
    {
        var controller = (JuanFootPlanting)command.context;
        controller.ConfigureRig();
        EditorUtility.SetDirty(controller);
    }
#endif

    public void ConfigureRig()
    {
        manager = GetComponent<IKManager2D>();
        hips = FindChild("body_1");
        foreach (var solver in GetComponentsInChildren<LimbSolver2D>(true))
        {
            string name = solver.name.ToLowerInvariant();
            if (name == "leg left solver") leftLeg = solver;
            else if (name == "left right solver") rightLeg = solver;
            else if (name == "forearm left solver") leftArm = solver;
            else if (name == "forearm right solver") rightArm = solver;
        }
        ValidateSolver(leftLeg);
        ValidateSolver(rightLeg);
        ValidateSolver(leftArm);
        ValidateSolver(rightArm);
        if (!manager || !hips || !leftLeg || !rightLeg)
            throw new InvalidOperationException("JuanFootPlanting requires body_1 and both leg solvers.");

        leftFootHomeLocal = LocalTarget(leftLeg);
        rightFootHomeLocal = LocalTarget(rightLeg);
        hipsRestLocalPosition = hips.localPosition;
        hipsRestLocalRotation = hips.localRotation;
        legLengthLocal = ChainLengthLocal(leftLeg);
        leftLegRootRestLocal = WorldToLocal(leftLeg.GetChain(0).transforms[0].position);
        rightLegRootRestLocal = WorldToLocal(rightLeg.GetChain(0).transforms[0].position);
        ResetTracking();
    }

    void OnEnable()
    {
        try { if (!IsConfigured) ConfigureRig(); }
        catch (Exception exception) { Debug.LogError(exception, this); enabled = false; return; }
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

    void LateUpdate() { if (Application.IsPlaying(gameObject)) Tick(Time.deltaTime); }

#if UNITY_EDITOR
    void EditorTick()
    {
        double now = EditorApplication.timeSinceStartup;
        float dt = (float)(now - editorTime);
        editorTime = now;
        if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || !this
            || !isActiveAndEnabled || EditorUtility.IsPersistent(this)
            || PrefabStageUtility.GetPrefabStage(gameObject) != null) return;
        if (!previewInScene || dt <= 0f || dt > 0.2f) { RestorePose(); ResetTracking(); return; }
        Tick(dt);
        manager.UpdateManager();
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }

    void BeforeSceneSave(UnityEngine.SceneManagement.Scene scene, string path)
    {
        if (scene == gameObject.scene) { RestorePose(); ResetTracking(); }
    }
#endif

public void ResetTracking()
    {
        previousRootPosition = transform.position;
        previousScale = transform.lossyScale;
        speed = runBlend = pelvisDrop = idleBlend = travelSinceStep = 0f;
        directionSign = 1f;
        lastStepped = -1;
        ResetFoot(leftFoot);
        ResetFoot(rightFoot);
        ready = true;
    }

public void Tick(float dt)
    {
        if (!IsConfigured || dt <= 0f) return;
        if (!ready) ResetTracking();

        Vector3 delta = transform.position - previousRootPosition;
        previousRootPosition = transform.position;
        float rootScaleX = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
        if ((transform.lossyScale - previousScale).sqrMagnitude > 0.00001f
            || delta.magnitude > WorldLength(legLengthLocal) * 2f || dt > 0.2f)
        {
            RestorePose();
            ResetTracking();
            return;
        }

        float horizontalDistance = Mathf.Abs(delta.x);
        float localTravel = horizontalDistance / rootScaleX;
        float measuredSpeed = horizontalDistance / dt;
        speed = Mathf.Lerp(speed, measuredSpeed, 1f - Mathf.Exp(-speedResponse * dt));
        if (horizontalDistance > 0.00001f) directionSign = Mathf.Sign(delta.x);
        runBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(runStartSpeed,
            Mathf.Max(runStartSpeed + 0.01f, fullRunSpeed), speed));

        RestoreHip();
        EnsureInitialized(leftLeg, leftFoot);
        EnsureInitialized(rightLeg, rightFoot);

        if (measuredSpeed > stopSpeed || speed > stopSpeed) UpdateWalking(dt, localTravel);
        else
        {
            Advance(leftFoot, dt);
            Advance(rightFoot, dt);
            if (!leftFoot.stepping && !rightFoot.stepping) SettleToStanding(dt);
        }

        ApplyPelvis(dt);
        ApplyFoot(leftLeg, leftFoot);
        ApplyFoot(rightLeg, rightFoot);
        if (idleBlend > 0f) ApplyIdleArms();
    }

void UpdateWalking(float dt, float localTravel)
    {
        idleBlend = 0f;
        travelSinceStep += localTravel;
        Advance(leftFoot, dt);
        Advance(rightFoot, dt);
        if (leftFoot.stepping || rightFoot.stepping) return;

        float stride = legLengthLocal * Mathf.Lerp(walkStride, runStride, runBlend);
        float interval = Mathf.Max(0.01f, stride * 0.5f);
        Vector3 leftDesired = DesiredLocal(0);
        Vector3 rightDesired = DesiredLocal(1);
        float leftError = Mathf.Abs(WorldToLocal(leftFoot.plantedWorld).x - leftDesired.x);
        float rightError = Mathf.Abs(WorldToLocal(rightFoot.plantedWorld).x - rightDesired.x);
        bool needsRecovery = Mathf.Max(leftError, rightError) >= legLengthLocal * stepError;

        if (travelSinceStep < interval && !needsRecovery) return;

        int candidate = ChooseFoot(leftError, rightError);
        StartStep(candidate, candidate == 0 ? leftFoot : rightFoot,
            candidate == 0 ? leftLeg : rightLeg, candidate == 0 ? leftDesired : rightDesired);
        travelSinceStep = Mathf.Max(0f, travelSinceStep - interval);
    }

int ChooseFoot(float leftError, float rightError)
    {
        // Once walking has begun, alternate feet; use the larger error only for recovery.
        if (lastStepped == 0) return 1;
        if (lastStepped == 1) return 0;
        return leftError >= rightError ? 0 : 1;
    }

    void StartStep(int index, FootState foot, LimbSolver2D solver, Vector3 desiredLocal)
    {
        foot.stepping = true;
        foot.progress = 0f;
        foot.duration = Mathf.Lerp(walkStepDuration, runStepDuration, runBlend);
        foot.swingStartWorld = foot.plantedWorld;
        foot.landingWorld = ClampToReach(solver, transform.TransformPoint(desiredLocal));
        lastStepped = index;
    }

Vector3 DesiredLocal(int footIndex)
    {
        // For a side-walk both feet use the same central travel track. The planted
        // foot stays behind while the other crosses past it to the next forward step.
        Vector3 center = (leftFootHomeLocal + rightFootHomeLocal) * 0.5f;
        float stride = legLengthLocal * Mathf.Lerp(walkStride, runStride, runBlend);
        float smallSideOffset = footIndex == 0 ? walkTrackOffset : -walkTrackOffset;
        center.x += legLengthLocal * smallSideOffset;
        center.x += directionSign * stride * landingLead;
        return center;
    }

    void Advance(FootState foot, float dt)
    {
        if (!foot.stepping) return;
        foot.progress = Mathf.Clamp01(foot.progress + dt / Mathf.Max(0.01f, foot.duration));
        if (foot.progress >= 1f) { foot.plantedWorld = foot.landingWorld; foot.stepping = false; }
    }

void SettleToStanding(float dt)
    {
        idleBlend = Mathf.MoveTowards(idleBlend, 1f, dt * settleSpeed);

        // Use the authored, un-solved leg roots. Each target is at full leg reach
        // directly below that root, so the thigh and shin form one straight line.
        Vector3 leftStanding = leftLegRootRestLocal + Vector3.down * ChainLengthLocal(leftLeg);
        Vector3 rightStanding = rightLegRootRestLocal + Vector3.down * ChainLengthLocal(rightLeg);
        leftStanding.z = leftFootHomeLocal.z;
        rightStanding.z = rightFootHomeLocal.z;

        leftFoot.plantedWorld = Vector3.Lerp(leftFoot.plantedWorld, transform.TransformPoint(leftStanding), idleBlend);
        rightFoot.plantedWorld = Vector3.Lerp(rightFoot.plantedWorld, transform.TransformPoint(rightStanding), idleBlend);
    }

void ApplyFoot(LimbSolver2D solver, FootState foot)
    {
        Vector3 target = foot.plantedWorld;
        if (foot.stepping)
        {
            target = Vector3.Lerp(foot.swingStartWorld, foot.landingWorld, Smooth(foot.progress));
            float lift = WorldLength(legLengthLocal * Mathf.Lerp(walkLift, runLift, runBlend));
            target += Vector3.up * (Mathf.Sin(foot.progress * Mathf.PI) * lift);
        }

        var chain = solver.GetChain(0);
        // An idle foot is deliberately at exact full reach; do not shorten it with
        // the normal walking safety clamp or the knee will remain bent.
        chain.target.position = idleBlend > 0f && !foot.stepping
            ? target
            : ClampToReach(solver, target);

        if (!foot.stepping)
            chain.target.rotation = Quaternion.Euler(0f, 0f, idleFootAngle);
    }

    void ApplyIdleArms()
    {
        PoseIdleArm(leftArm, 1f);
        PoseIdleArm(rightArm, -1f);
    }

    void PoseIdleArm(LimbSolver2D solver, float side)
    {
        var chain = solver.GetChain(0);
        Vector3 root = WorldToLocal(chain.transforms[0].position);
        float length = ChainLengthLocal(solver);
        Vector3 target = root + Vector3.right * (side * length * idleArmSideOffset)
            + Vector3.down * (length * idleArmDrop);
        chain.target.position = ClampToReach(solver, transform.TransformPoint(target));
        chain.target.rotation = Quaternion.identity;
    }

void ApplyPelvis(float dt)
    {
        // Pelvis compensation protects walking reach. It must be off in the
        // standing pose, otherwise it bends an otherwise fully extended leg.
        float desiredDrop = 0f;
        if (idleBlend <= 0f)
        {
            float excess = Mathf.Max(ReachExcess(leftLeg, CurrentTarget(leftFoot)),
                ReachExcess(rightLeg, CurrentTarget(rightFoot)));
            desiredDrop = Mathf.Clamp(excess, 0f, legLengthLocal * maxPelvisDrop);
        }

        pelvisDrop = Mathf.Lerp(pelvisDrop, desiredDrop, 1f - Mathf.Exp(-pelvisResponse * dt));
        hips.localPosition = hipsRestLocalPosition + Vector3.down * pelvisDrop;
        hips.localRotation = hipsRestLocalRotation;
    }

    Vector3 CurrentTarget(FootState foot) => foot.stepping
        ? Vector3.Lerp(foot.swingStartWorld, foot.landingWorld, Smooth(foot.progress))
        : foot.plantedWorld;

    float ReachExcess(LimbSolver2D solver, Vector3 worldTarget)
    {
        var chain = solver.GetChain(0);
        return Mathf.Max(0f, Vector3.Distance(WorldToLocal(chain.transforms[0].position), WorldToLocal(worldTarget))
            - ChainLengthLocal(solver) * comfortableReach);
    }

    Vector3 ClampToReach(LimbSolver2D solver, Vector3 worldTarget)
    {
        var chain = solver.GetChain(0);
        Vector3 origin = WorldToLocal(chain.transforms[0].position);
        Vector3 desired = WorldToLocal(worldTarget);
        return transform.TransformPoint(origin + Vector3.ClampMagnitude(desired - origin, ChainLengthLocal(solver) * 0.985f));
    }

    void EnsureInitialized(LimbSolver2D solver, FootState foot)
    {
        if (foot.initialized) return;
        foot.initialized = true;
        foot.plantedWorld = foot.swingStartWorld = foot.landingWorld = solver.GetChain(0).target.position;
    }

    void ResetFoot(FootState foot) { foot.initialized = foot.stepping = false; foot.progress = foot.duration = 0f; }

    void RestoreHip()
    {
        if (!hips) return;
        hips.localPosition = hipsRestLocalPosition;
        hips.localRotation = hipsRestLocalRotation;
    }

    public void RestorePose()
    {
        RestoreHip();
        if (leftLeg) leftLeg.GetChain(0).target.position = transform.TransformPoint(leftFootHomeLocal);
        if (rightLeg) rightLeg.GetChain(0).target.position = transform.TransformPoint(rightFootHomeLocal);
    }

    Transform FindChild(string name)
    {
        foreach (var child in GetComponentsInChildren<Transform>(true))
            if (child.name == name) return child;
        return null;
    }

    static void ValidateSolver(LimbSolver2D solver)
    {
        if (solver && !solver.isValid) solver.Initialize();
        if (!solver || !solver.isValid || !solver.GetChain(0).target)
            throw new InvalidOperationException("Juan has an invalid leg IK chain.");
    }

    float ChainLengthLocal(LimbSolver2D solver)
    {
        var chain = solver.GetChain(0);
        return Vector3.Distance(WorldToLocal(chain.transforms[0].position), WorldToLocal(chain.transforms[1].position))
            + Vector3.Distance(WorldToLocal(chain.transforms[1].position), WorldToLocal(chain.effector.position));
    }

    Vector3 LocalTarget(LimbSolver2D solver) => WorldToLocal(solver.GetChain(0).target.position);
    Vector3 WorldToLocal(Vector3 world) => transform.InverseTransformPoint(world);
    float WorldLength(float localLength) => transform.TransformVector(Vector3.right * localLength).magnitude;
    static float Smooth(float t) => t * t * (3f - 2f * t);
}