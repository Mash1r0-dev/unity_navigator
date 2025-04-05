using UnityEngine;

public class WASDMoveCube : MonoBehaviour
{
    public float speed = 5f;

    void Update()
    {
        float horizontalInput = 0f;
        float verticalInput = 0f;

        if (Input.GetKey(KeyCode.W))
        {
            verticalInput = 1f;
        }
        else if (Input.GetKey(KeyCode.S))
        {
            verticalInput = -1f;
        }
        if (Input.GetKey(KeyCode.A))
        {
            horizontalInput = -1f;
        }
        else if (Input.GetKey(KeyCode.D))
        {
            horizontalInput = 1f;
        }
        Vector3 movement = new Vector3(horizontalInput, 0f, verticalInput);
        transform.Translate(movement * speed * Time.deltaTime);
    }
}