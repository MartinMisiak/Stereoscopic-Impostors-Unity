using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Helper struct for the caching of projection results
// e.g. projecting the impostors bbox onto the screen is done multiple times in a frame
struct ProjectionResults
{
    internal ProjectionResults(float init)
    {
        this.minX = init;
        this.maxX = init;
        this.minY = init;
        this.maxY = init;
        this.minZ = init;
        this.maxZ = init;
        this.resX = init;
        this.resY = init;
        this.projMat = new Matrix4x4();
    }
    internal float minX;
    internal float maxX;
    internal float minY;
    internal float maxY;
    internal float minZ;
    internal float maxZ;
    internal float resX;
    internal float resY;
    internal Matrix4x4 projMat;
}

// Used to store all information, unique to a single impostor
// e.g. a stereoscopic impostor uses one ImpostorMetadata for each eye
class ImpostorMetadata
{
    public ImpostorMetadata(ImpostorGenerator ig)
    {
        this.generator = ig;
        this.previousCaptureViewMatrix = new Matrix4x4();
        this.previousCaptureProjectionMatrix = new Matrix4x4();
        this.nextCaptureViewMatrix = new Matrix4x4();
        this.currentCaptureViewMatrix = new Matrix4x4();
        this.currentCaptureProjectionMatrix = new Matrix4x4();
        this.nextCaptureProjectionMatrix = new Matrix4x4();
        this.previousCaptureProjectionMatrix = new Matrix4x4();
        this.previousTextureBounds     = Vector4.negativeInfinity;
        this.currentTextureBounds      = Vector4.negativeInfinity;
        this.nextTextureBounds         = Vector4.negativeInfinity;
        this.nextCapturePosition = new Vector3();
    }

    public ImpostorGenerator generator;
    public Matrix4x4 previousCaptureViewMatrix;
    public Matrix4x4 previousCaptureProjectionMatrix;
    public Matrix4x4 currentCaptureViewMatrix;
    public Matrix4x4 currentCaptureProjectionMatrix;
    public Matrix4x4 nextCaptureViewMatrix;
    public Matrix4x4 nextCaptureProjectionMatrix;
    public Vector4 previousTextureBounds;
    public Vector4 currentTextureBounds;
    public Vector4 nextTextureBounds;
    public Vector3 nextCapturePosition;

}

public enum ImpostorState
{
    IDLE,
    GENERATING,// Impostor is being regenerated in this frame
    FINISHING, // FINISHING is currently a legacy state, since the regeneration is currently done in one frame
    BLENDING   // Impostor is finished regenerating and is currently being rendered by blending the current and previous atlas tiles together
}

// Different impostor rendering modes
public enum ImpostorMode
{
    MONO = 0,            // Impostor captured only from one perspective (left eye). Impostor is rendered as a capture camera facing 2D quad 
    MONO_PARALLAX = 1,   // Impostor captured only from one perspective (left eye), reprojected into other views based on depth information
    STEREO = 2,          // Impostor is captured from two perspectives (left & right eye). Each impostor is rendered as a capture camera facing 2D quad.
    STEREO_PARALLAX = 3, // Impostor is captured from two perspectives (left & right eye), reprojected into other views based on depth information of current eye
    AUTOMATIC = 4        // The Impostor-Manager can decide which impostor mode is currently appropriate
}

// ValidityField is used for the parallax term in the regeneration metric.
// This is a simple interface for implementing different parallax terms, which are easily swappable
interface ValidityField
{
    ImpostorGenerator getImpostorGenerator();
    void computeValidityField(Vector3 capturePosition);
    float queryValidityField(Vector3 viewerPosition);
}

// Very simple validity metric, where the angle between the capture direction
// and the current viewer direction is considered.

class NaiveAngleValidityField : ValidityField
{
    private Vector3 captureDirection;
    private ImpostorGenerator igc;

    internal NaiveAngleValidityField(ImpostorGenerator igc)
    {
        this.igc      = igc;
        captureDirection = Vector3.zero;
    }

    public void computeValidityField(Vector3 capturePosition)
    {
        this.captureDirection = Vector3.Normalize( capturePosition - this.igc.getImpostorBounds().center );
    }

    public ImpostorGenerator getImpostorGenerator()
    {
        return this.igc;
    }

    public float queryValidityField(Vector3 viewerPosition)
    {
        Vector3 currentDirection = Vector3.Normalize( viewerPosition - this.igc.getImpostorBounds().center );
        float angle = Mathf.Abs( Vector3.Angle(currentDirection, this.captureDirection) );
        return ( angle / ImpostorManager.NAIVE_ANGLE_METRIC_THRESHOLD);
    }
}

// Validity metric by [Shade et al. 96]
// "Hierarchical image caching for accelerated walkthroughs of complex environments"
class BboxProjectionShade : ValidityField
{
    private Vector3 viewCellCenter;
    private ImpostorGenerator igc;

    internal BboxProjectionShade(ImpostorGenerator igc)
    {
        this.igc = igc;
        this.viewCellCenter = new Vector3();
    }

    
    public ImpostorGenerator getImpostorGenerator()
    {
        return this.igc;
    }

    public void computeValidityField(Vector3 capturePosition)
    {
        Bounds bounds = igc.getImpostorBounds();
        Vector3 minBB = bounds.min;
        Vector3 maxBB = bounds.max;
        Vector3 centerBB = (maxBB + minBB) * 0.5f;

        //Store BB-Center to CapturePosition Offset in viewCellCenter
        this.viewCellCenter = capturePosition - centerBB;
    }

    private bool intersectPlane(Vector3 rayOrigin, Vector3 rayDir, Vector3 planeOrigin, Vector3 planeNormal, out float t)
    {
        t = 0;
        float denom = Vector3.Dot(rayDir, planeNormal);
        if (Mathf.Abs(denom) > 0.00001f)
        {
            Vector3 temp = planeOrigin - rayOrigin;
            float new_t = Vector3.Dot(temp, planeNormal) / denom;
            t = new_t;

            if (new_t >= 0)
                return true;
        }
        return false;
    }

    public float queryValidityField(Vector3 viewerPosition)
    {
        Bounds bounds = igc.getImpostorBounds();
        Vector3 minBB = bounds.min;
        Vector3 maxBB = bounds.max;
        Vector3 centerBB = (maxBB + minBB) * 0.5f;
        Vector3 capturePosition = this.viewCellCenter + centerBB;
        Vector3 planeNormal = Vector3.Normalize(viewCellCenter);
        Vector3 planeOrigin = planeNormal * Vector3.Dot(planeNormal, centerBB);

        //Reconstruct all BBox points
        Vector3[] BB_points = new Vector3[8];
        BB_points[0] = new Vector3(minBB.x, minBB.y, minBB.z);
        BB_points[7] = new Vector3(maxBB.x, maxBB.y, maxBB.z);
        float diffX = BB_points[7].x - BB_points[0].x;
        float diffY = BB_points[7].y - BB_points[0].y;
        float diffZ = BB_points[7].z - BB_points[0].z;
        BB_points[1] = BB_points[0] + new Vector3(diffX, 0, 0);
        BB_points[2] = BB_points[0] + new Vector3(0, 0, diffZ);
        BB_points[3] = BB_points[0] + new Vector3(diffX, 0, diffZ);
        BB_points[4] = BB_points[0] + new Vector3(0, diffY, 0);
        BB_points[5] = BB_points[0] + new Vector3(0, diffY, diffZ);
        BB_points[6] = BB_points[0] + new Vector3(diffX, diffY, 0);

        float maxAngle = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            Vector3 rayDirA = BB_points[i] - viewerPosition;
            rayDirA.Normalize();
            Vector3 rayDirB = BB_points[i] - capturePosition;
            rayDirB.Normalize();

            //planeOrigin is the point on the plane that is closest to vec3( 0.0 ),
            float t = 0;
            bool hit = intersectPlane(capturePosition, rayDirB, planeOrigin, planeNormal, out t);
            if (!hit)
            {
                maxAngle = 90;
            }

            Vector3 hitpoint = capturePosition + (rayDirB * t);
            Vector3 rayDirC = hitpoint - viewerPosition;
            rayDirC.Normalize();

            if (Vector3.Dot(rayDirC, planeNormal) > 0)
            {
                maxAngle = 90;
            }
            
            float angle = Vector3.Angle(rayDirC, rayDirA);;
            if (angle > maxAngle)
                maxAngle = angle;
        }
        
        return (maxAngle / ImpostorManager.PROJECTED_BBOX_METRIC_THRESHOLD);
    }
}