using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class UtilityFunctions
{
    public static void DrawLine(Vector3 start, Vector3 end, Color color, float duration = 0.2f)
    {
        GameObject myLine = new GameObject();
        myLine.transform.position = start;
        myLine.AddComponent<LineRenderer>();
        LineRenderer lr = myLine.GetComponent<LineRenderer>();
        lr.material = new Material(Shader.Find("Unlit/Color"));
        lr.startColor = color;
        lr.endColor = color;
        lr.startWidth = 0.1f;
        lr.endWidth = 0.1f;
        lr.SetPosition(0, start);
        lr.SetPosition(1, end);
        GameObject.Destroy(myLine, duration);
    }

    public static void DrawBBoxLines(Vector3[] points, Color color, float duration)
    {
        if (points.Length != 8)
        {
            Debug.Log("There are not exactly 8 points in the input array!");
            return;
        }

        DrawLine(points[0], points[1], color, duration);
        DrawLine(points[1], points[2], color, duration);
        DrawLine(points[2], points[3], color, duration);
        DrawLine(points[3], points[0], color, duration);

        DrawLine(points[4], points[5], color, duration);
        DrawLine(points[5], points[6], color, duration);
        DrawLine(points[6], points[7], color, duration);
        DrawLine(points[7], points[4], color, duration);

        DrawLine(points[0], points[4], color, duration);
        DrawLine(points[1], points[5], color, duration);
        DrawLine(points[2], points[6], color, duration);
        DrawLine(points[3], points[7], color, duration);
    }


    //https://answers.unity.com/questions/361275/cant-convert-bounds-from-world-coordinates-to-loca.html
    public static Bounds LocalToWorldBounds(Transform _transform, Bounds _localBounds)
    {
        var center = _transform.TransformPoint(_localBounds.center);

        // transform the local extents' axes
        var extents = _localBounds.extents;
        var axisX = _transform.TransformVector(extents.x, 0, 0);
        var axisY = _transform.TransformVector(0, extents.y, 0);
        var axisZ = _transform.TransformVector(0, 0, extents.z);

        // sum their absolute value to get the world extents
        extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
        extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
        extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

        return new Bounds { center = center, extents = extents };
    }



}