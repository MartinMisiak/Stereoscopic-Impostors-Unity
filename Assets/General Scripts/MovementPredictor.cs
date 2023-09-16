using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Labs.SuperScience;

public class MovementPredictor : MonoBehaviour
{
    private PhysicsTracker userTracker = new PhysicsTracker();
    // Start is called before the first frame update
    void Start()
    {
        
    }

    public Vector3 getNextOffsetPrediction(int framesInFuture)
    {
        Vector3 result = this.userTracker.Velocity * Time.fixedDeltaTime * (framesInFuture);
        return result;
    }

    // Update is called once per frame
    void Update()
    {
        this.userTracker.Update(this.transform.position, this.transform.rotation, Time.fixedDeltaTime);
    }
}
