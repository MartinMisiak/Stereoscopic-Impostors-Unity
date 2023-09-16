using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SimpleOrbitComponent : MonoBehaviour
{

    public GameObject target;
    public Vector3 rotationAngle = Vector3.up;
    public float rotationSpeed = 1f;
    public bool lookAt = true;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        this.transform.RotateAround(target.transform.position, rotationAngle, Time.deltaTime * rotationSpeed);
        if(lookAt)
            this.transform.LookAt(target.transform.position, Vector3.up);
    }
}
