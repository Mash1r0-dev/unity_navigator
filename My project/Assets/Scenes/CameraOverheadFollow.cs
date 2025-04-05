using UnityEngine;
public class CameraOverheadFollow : MonoBehaviour
{
    public Transform target;
    public float heightOffset = 5f;

    void LateUpdate()
    {
        if (target != null)
        {

            Vector3 targetPosition = target.position;
            targetPosition.y += heightOffset;
            transform.position = targetPosition;
            transform.LookAt(target);
        }
    }
}