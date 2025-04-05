
using UnityEngine;
using System.Collections.Generic;

public class CubeStateBacktrack : MonoBehaviour
{
 
    private struct CubeState
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localScale;
    }

    private Queue<CubeState> stateHistory = new Queue<CubeState>();
    private const float recordInterval = 0.1f;
    private float timer = 0f;

    void Update()
    {
       
        timer += Time.deltaTime;
        if (timer >= recordInterval)
        {
            RecordState();
            timer = 0f;
        }

        
        if (Input.GetKeyDown(KeyCode.R))
        {
            BacktrackToState(5f);
        }
    }

  
    private void RecordState()
    {
        CubeState currentState = new CubeState
        {
            position = transform.position,
            rotation = transform.rotation,
            localScale = transform.localScale
        };
        stateHistory.Enqueue(currentState);

        while (stateHistory.Count * recordInterval > 5f)
        {
            stateHistory.Dequeue();
        }
    }
    private void BacktrackToState(float secondsAgo)
    {
        int statesToRemove = Mathf.FloorToInt(secondsAgo / recordInterval);
        while (stateHistory.Count > statesToRemove && statesToRemove > 0)
        {
            stateHistory.Dequeue();
            statesToRemove--;
        }

        if (stateHistory.Count > 0)
        {
            CubeState targetState = stateHistory.Peek();
            transform.position = targetState.position;
            transform.rotation = targetState.rotation;
            transform.localScale = targetState.localScale;
        }
    }
}