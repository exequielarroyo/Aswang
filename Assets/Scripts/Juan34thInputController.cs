using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Drives Juan 34th from the Player map in InputSystem_Actions.
/// PlayerInput sends the Move and Sprint actions to the methods below.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public sealed class Juan34thInputController : MonoBehaviour
{
    [Header("Movement")]
    [Min(0.01f)] public float walkSpeed = 2.5f;
    [Min(0.01f)] public float runSpeed = 5f;
    [Min(0f)] public float inputDeadZone = 0.01f;

    PlayerInput playerInput;
    InputAction sprintAction;
    Animator animator;
    Vector2 moveInput;
    bool sprintHeld;
    float facingSign = -1f;
    Vector3 authoredScale;

    static readonly int Speed = Animator.StringToHash("Speed");

void Awake()
    {
        animator = GetComponent<Animator>();
        playerInput = GetComponent<PlayerInput>();
        sprintAction = playerInput != null && playerInput.actions != null
            ? playerInput.actions.FindAction("Sprint", false)
            : null;
        authoredScale = transform.localScale;
        facingSign = authoredScale.x < 0f ? 1f : -1f;
    }

    /// <summary>PlayerInput callback for InputSystem_Actions/Player/Move.</summary>
    public void OnMove(InputValue value)
    {
        moveInput = value.Get<Vector2>();
    }

    /// <summary>PlayerInput callback for InputSystem_Actions/Player/Sprint.</summary>
    public void OnSprint(InputValue value)
    {
        sprintHeld = value.isPressed;
    }

void Update()
    {
        // Poll the action as the source of truth. This prevents a missed cancelled
        // callback from leaving the character in sprint mode after Shift is released.
        if (sprintAction == null && playerInput != null && playerInput.actions != null)
            sprintAction = playerInput.actions.FindAction("Sprint", false);
        if (sprintAction != null)
            sprintHeld = sprintAction.IsPressed();

        Vector2 direction = moveInput.sqrMagnitude > 1f ? moveInput.normalized : moveInput;
        float strength = direction.magnitude;
        bool moving = strength > inputDeadZone;
        float targetSpeed = moving ? (sprintHeld ? runSpeed : walkSpeed) * strength : 0f;

        // The source art faces left. Mirror only for rightward movement; retain the
        // last horizontal facing while travelling vertically.
        if (Mathf.Abs(direction.x) > inputDeadZone)
            facingSign = direction.x > 0f ? -1f : 1f;

        Vector3 scale = authoredScale;
        scale.x = Mathf.Abs(authoredScale.x) * facingSign;
        transform.localScale = scale;

        transform.position += new Vector3(direction.x, direction.y, 0f)
            * (sprintHeld ? runSpeed : walkSpeed) * Time.deltaTime;
        animator.SetFloat(Speed, targetSpeed);
    }

    void OnDisable()
    {
        if (animator != null)
            animator.SetFloat(Speed, 0f);
    }
}