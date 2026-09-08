using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public InputAction MoveAction;
    Animator animator;
    Rigidbody2D rigidbody2d;
    Vector2 move;

    [SerializeField]
    private float speed = 3.0f;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        //QualitySettings.vSyncCount = 0;
        //Application.targetFrameRate = 10;
        MoveAction.Enable();
        animator = GetComponent<Animator>();
        rigidbody2d = GetComponent<Rigidbody2D>();
    }

    // Update is called once per frame
void Update()
    {
        // Read the movement input from the configured MoveAction.
        move = MoveAction.ReadValue<Vector2>();


        // Remember the last horizontal direction for the correct idle animation.
        if (move.x != 0)
        {
            animator.SetFloat("Facing", Mathf.Sign(move.x));
        }

        
// Use movement amount to switch between idle and walking states.
        animator.SetFloat("Speed", move.magnitude);

        
// Feed movement into the walking Blend Tree.
        animator.SetFloat("Move X", move.x);
        animator.SetFloat("Move Y", move.y);

        // Original movement examples:
        // Vector2 position = transform.position;
        // position.x = position.x + 0.01f;
        // position.y = position.y + 0.01f;
        // transform.position = position;
        //
        // Vector2 position = (Vector2)transform.position + move * 0.01f;
        // Vector2 position = (Vector2)transform.position + move * 0.01f * Time.deltaTime;
    }

    void FixedUpdate()
    {
        Vector2 position = rigidbody2d.position + move * speed * Time.deltaTime;
        rigidbody2d.MovePosition(position);
    }
}
