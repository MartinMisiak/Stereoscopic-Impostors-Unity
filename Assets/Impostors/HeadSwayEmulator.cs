using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Helper component, used to emulate VR-like head sway movements on a camera. 
// Useful for emulating aliasing experienced in VR
public class HeadSwayEmulator : MonoBehaviour
{
    public float swayStrength = 0.01f;
    public float swayDuration = 3f;
    private float counter      = 0f;
    private Vector3  currentSwayDirection = new Vector3(0,0,0);


    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
         if(counter >= swayDuration)
        {
            currentSwayDirection = Random.onUnitSphere;
            currentSwayDirection *= swayStrength;
            counter = 0;
        }

        float deltaTime    = Time.deltaTime;
        Vector3  deltaSway = currentSwayDirection * deltaTime;
        this.transform.Translate( deltaSway );
        counter += deltaTime;
    }
}
