using UnityEngine;

public class shijianCamera : MonoBehaviour
{
    public Transform target;
    public float distance = 5f; 
    public float height = 2f; 
    public float smoothSpeed = 0.125f; 
    public float rotationDamping = 3f; 

    private void LateUpdate()
    {
        if (target == null)
        {
            return;
        }
        Vector3 targetPosition = target.position - target.forward * distance + Vector3.up * height;
        transform.position = Vector3.Lerp(transform.position, targetPosition, smoothSpeed);
        Quaternion targetRotation = Quaternion.LookRotation(target.position - transform.position);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationDamping * Time.deltaTime);
    }
}