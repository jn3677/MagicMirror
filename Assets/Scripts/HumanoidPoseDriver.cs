using System.Collections.Generic;
using UnityEngine;

// Drives a Humanoid avatar from the metric camera-space landmarks published by
// MediaPipeBodyTracker. The hips and chest are rotated to match the tracked
// body frames, and the avatar's rest pose is captured at Start, so the tracked
// pose is applied as a delta without an animation controller.
//
// The upper and lower limb bones are solved with two-bone IK: the shoulder or
// hip reaches for the tracked wrist or ankle, while a pole picks the elbow or
// knee bend. The arm pole follows the tracked elbow, so the sleeve folds where
// the real elbow folds without the elbow's depth noise moving the hand; the
// knee keeps a fixed body-relative pole. Feet and toes still follow their
// tracked directions.
//
// The avatar is fitted per bone instead of by one uniform scale. At Start the
// length of each tracked segment is measured on the avatar's own skeleton, and
// each bone is scaled until that segment matches the same segment on the
// person. The torso supplies the overall scale, the clavicles set the shoulder
// width, and the arms and legs hold their own proportions, so a short torso or
// long arms keep their own shape instead of inheriting a shoulder width.
//
// The fit is measured from the first seconds of good tracking and then locked.
// A depth sample is good enough to size a body once, but not to resize it every
// frame: depth on a segment that points at the camera comes from whatever
// surface the sensor happened to see, so a live fit breathes and stretches with
// the pose instead of staying on the person.
//
// The avatar is anchored at the tracked shoulders, which are in meters, so
// walking toward or away from the mirror moves it without resizing it. It is
// pushed half its width deeper than the shoulders so it sits behind the person.
public class HumanoidPoseDriver : MonoBehaviour
{
    public bool HasValidPose { get; private set; }

    // True once the avatar has been fitted to a measured body.
    public bool IsFitted { get; private set; }

    // Discards the previous fit so the next body is measured from scratch.
    public void ResetTracking()
    {
        HasValidPose = false;
        ResetFit();
    }

    private const int LandmarkCount = 33;
    private const float MinimumDirectionSqrMagnitude = 0.0001f;
    private const float MinimumLimbLength = 0.0001f;

    // How far outside the shoulder line the fallback elbow pole sits, in body
    // units.
    private const float ArmPoleOutward = 0.3f;

    // An elbow this close to the shoulder-wrist line leaves the bend direction
    // undefined, so the fixed pole is used instead.
    private const float MinimumBendSqrMagnitude = 0.0004f;

    // Landmark depths further apart than this cannot belong to one body.
    private const float MaximumDepthSpread = 0.5f;

    private const float FitSmoothingSeconds = 0.2f;
    private const float FitResetSeconds = 2f;

    // A segment that differs from the torso by more than this is a bad depth
    // sample, not a differently proportioned body.
    private const float MinimumProportionRatio = 0.5f;
    private const float MaximumProportionRatio = 2f;

    // A segment leaning further toward the camera than this share of its own
    // length takes most of that length from depth, so it does not measure the
    // body and must not be locked in as a proportion.
    private const float MaximumForeshorteningRatio = 0.5f;

    private const int LeftShoulderLandmark = 11;
    private const int RightShoulderLandmark = 12;
    private const int LeftElbowLandmark = 13;
    private const int RightElbowLandmark = 14;
    private const int LeftWristLandmark = 15;
    private const int RightWristLandmark = 16;
    private const int LeftHipLandmark = 23;
    private const int RightHipLandmark = 24;
    private const int LeftKneeLandmark = 25;
    private const int RightKneeLandmark = 26;
    private const int LeftAnkleLandmark = 27;
    private const int RightAnkleLandmark = 28;
    private const int LeftFootLandmark = 31;
    private const int RightFootLandmark = 32;

    [Header("Sources")]
    [SerializeField]
    private MediaPipeBodyTracker bodyTracker;

    [SerializeField]
    private Animator targetAnimator;

    [Header("Placement")]
    [Tooltip("Move the avatar so its shoulders sit on the tracked shoulders.")]
    [SerializeField]
    private bool followShoulderPosition = true;

    [Tooltip(
        "Scales tracked positions and the fitted avatar before they are applied " +
        "to the scene. 1 is real-world meters."
    )]
    [SerializeField]
    [Min(0f)]
    private float positionScale = 1f;

    [Tooltip(
        "Closest the avatar's shoulders are allowed to get to the camera. " +
        "Scaling the tracked position along its view ray keeps the avatar on " +
        "the same part of the screen while keeping its mesh in front of the " +
        "near plane."
    )]
    [SerializeField]
    [Min(0.1f)]
    private float minimumDistance = 0.6f;

    [Header("Motion")]
    [Tooltip(
        "Higher values follow the tracker more tightly. " +
        "0 applies every detection immediately."
    )]
    [SerializeField]
    [Min(0f)]
    private float smoothing = 12f;

    [Tooltip(
        "Seconds of good tracking to measure the proportions over before they " +
        "are locked. 0 locks on the first measurement. The fit is only measured " +
        "again after the body has been lost for a while."
    )]
    [SerializeField]
    [Min(0f)]
    private float fitLockSeconds = 1.5f;

    private struct LimbBone
    {
        public Transform Bone;
        public Vector3 RestLocalScale;
        public Quaternion RestLocalRotation;
        public Vector3 RestDirectionInParent;
        public float RestLength;
        public int StartLandmark;
        public int EndLandmark;
        public float SmoothedRatio;
        public bool HasRatio;
        public bool MatchesLength;
        public bool UsesIk;
    }

    // The upper and lower bone of one limb. Elbows and knees are solved from
    // the end target and a pole direction instead of following their noisy
    // tracked landmarks directly.
    private struct IkChain
    {
        public int UpperIndex;
        public int LowerIndex;
        public Transform End;
        public int RootLandmark;
        public int EndLandmark;
        public Vector3 PoleInBody;
        public bool IsArm;
    }

    private readonly List<LimbBone> limbBones = new List<LimbBone>();
    private readonly List<IkChain> ikChains = new List<IkChain>();

    private Transform hips;
    private Transform spine;
    private Transform chest;
    private Transform leftShoulder;
    private Transform rightShoulder;
    private Transform leftHip;
    private Transform rightHip;
    private Transform leftClavicle;
    private Transform rightClavicle;

    private Vector3 leftClavicleRestScale;
    private Vector3 rightClavicleRestScale;

    private Quaternion hipsRestRotation;
    private Quaternion spineRestRotation;
    private Quaternion chestRestRotation;

    // Rest body frames, built from the bone joints rather than the bone axes.
    private Quaternion restPelvisRotation;
    private Quaternion restChestRotation;

    private Transform trackingCamera;

    private readonly Vector3[] pose = new Vector3[LandmarkCount];
    private readonly bool[] poseValid = new bool[LandmarkCount];
    private readonly bool[] poseInFrame = new bool[LandmarkCount];

    private bool hasPlacedRoot;
    private bool hasTorsoRatio;
    private bool hasShoulderRatio;

    private Vector3 authoredScale;
    private float restShoulderWidth;
    private float restTorsoLength;

    private float torsoRatio = 1f;
    private float shoulderRatio = 1f;
    private float lastBodyTime = float.NegativeInfinity;

    private bool fitLocked;
    private float fitMeasuredSeconds;

    private void Start()
    {
        if (bodyTracker == null)
        {
            bodyTracker = FindAnyObjectByType<MediaPipeBodyTracker>();
        }

        if (targetAnimator == null)
        {
            targetAnimator = GetComponentInChildren<Animator>();
        }

        if (bodyTracker == null)
        {
            Debug.LogError(
                "HumanoidPoseDriver: no MediaPipeBodyTracker in the scene.",
                this
            );

            enabled = false;
            return;
        }

        if (targetAnimator == null || targetAnimator.avatar == null || !targetAnimator.isHuman)
        {
            Debug.LogError(
                "HumanoidPoseDriver: assign an Animator whose Avatar is Humanoid.",
                this
            );

            enabled = false;
            return;
        }

        if (!CacheTorso())
        {
            enabled = false;
            return;
        }

        CacheLimbs();
        CacheIkChains();

        if (!CacheFitReferences())
        {
            enabled = false;
            return;
        }

        if (followShoulderPosition)
        {
            Camera mainCamera = Camera.main;

            if (mainCamera != null)
            {
                trackingCamera = mainCamera.transform;
            }
            else
            {
                Debug.LogWarning(
                    "HumanoidPoseDriver: no Main Camera, so the avatar will stay in place.",
                    this
                );
            }
        }
    }

    private bool CacheTorso()
    {
        hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
        spine = targetAnimator.GetBoneTransform(HumanBodyBones.Spine);
        chest = targetAnimator.GetBoneTransform(HumanBodyBones.Chest);
        Transform neck = targetAnimator.GetBoneTransform(HumanBodyBones.Neck);
        Transform leftArm = targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightArm = targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform leftLeg = targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Transform rightLeg = targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperLeg);

        if (hips == null || spine == null || chest == null || neck == null ||
            leftArm == null || rightArm == null || leftLeg == null || rightLeg == null)
        {
            Debug.LogError(
                "HumanoidPoseDriver: the Humanoid avatar is missing a required bone.",
                this
            );

            return false;
        }

        hipsRestRotation = hips.rotation;
        spineRestRotation = spine.rotation;
        chestRestRotation = chest.rotation;

        leftShoulder = leftArm;
        rightShoulder = rightArm;
        leftHip = leftLeg;
        rightHip = rightLeg;

        leftClavicle = targetAnimator.GetBoneTransform(HumanBodyBones.LeftShoulder);
        rightClavicle = targetAnimator.GetBoneTransform(HumanBodyBones.RightShoulder);

        if (leftClavicle != null)
        {
            leftClavicleRestScale = leftClavicle.localScale;
        }

        if (rightClavicle != null)
        {
            rightClavicleRestScale = rightClavicle.localScale;
        }

        bool hasPelvisFrame = TryBuildBodyRotation(
            rightLeg.position - leftLeg.position,
            spine.position - hips.position,
            out restPelvisRotation
        );

        bool hasChestFrame = TryBuildBodyRotation(
            rightArm.position - leftArm.position,
            neck.position - chest.position,
            out restChestRotation
        );

        if (!hasPelvisFrame || !hasChestFrame)
        {
            Debug.LogError(
                "HumanoidPoseDriver: the avatar's rest pose has degenerate hips or shoulders.",
                this
            );

            return false;
        }

        return true;
    }

    private void CacheLimbs()
    {
        AddLimb(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
            LeftShoulderLandmark, LeftElbowLandmark, true);

        AddLimb(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            LeftElbowLandmark, LeftWristLandmark, true);

        AddLimb(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
            RightShoulderLandmark, RightElbowLandmark, true);

        AddLimb(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            RightElbowLandmark, RightWristLandmark, true);

        AddLimb(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
            LeftHipLandmark, LeftKneeLandmark, true);

        AddLimb(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            LeftKneeLandmark, LeftAnkleLandmark, true);

        // The toe landmark is the tip of the foot, while the toe bone ends at
        // its base, so measuring this segment would stretch the foot. It still
        // follows the shin's fit.
        AddLimb(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            LeftAnkleLandmark, LeftFootLandmark, false);

        AddLimb(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
            RightHipLandmark, RightKneeLandmark, true);

        AddLimb(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            RightKneeLandmark, RightAnkleLandmark, true);

        AddLimb(HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
            RightAnkleLandmark, RightFootLandmark, false);
    }

    private void AddLimb(
        HumanBodyBones boneId,
        HumanBodyBones childId,
        int startLandmark,
        int endLandmark,
        bool matchesLength)
    {
        Transform bone = targetAnimator.GetBoneTransform(boneId);
        Transform child = targetAnimator.GetBoneTransform(childId);

        if (bone == null || child == null || bone.parent == null)
        {
            return;
        }

        Vector3 restDirectionWorld = child.position - bone.position;

        if (restDirectionWorld.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            return;
        }

        limbBones.Add(new LimbBone
        {
            Bone = bone,
            RestLocalScale = bone.localScale,
            RestLocalRotation = bone.localRotation,
            RestDirectionInParent =
                Quaternion.Inverse(bone.parent.rotation) * restDirectionWorld.normalized,
            RestLength = restDirectionWorld.magnitude,
            StartLandmark = startLandmark,
            EndLandmark = endLandmark,
            MatchesLength = matchesLength
        });
    }

    private void CacheIkChains()
    {
        // Elbows point back and slightly outward; knees point forward.
        AddIkChain(
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            LeftShoulderLandmark,
            LeftWristLandmark,
            new Vector3(-ArmPoleOutward, 0f, -1f),
            true
        );

        AddIkChain(
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            RightShoulderLandmark,
            RightWristLandmark,
            new Vector3(ArmPoleOutward, 0f, -1f),
            true
        );

        AddIkChain(
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            LeftHipLandmark,
            LeftAnkleLandmark,
            Vector3.forward,
            false
        );

        AddIkChain(
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
            RightHipLandmark,
            RightAnkleLandmark,
            Vector3.forward,
            false
        );
    }

    private void AddIkChain(
        HumanBodyBones upperId,
        HumanBodyBones lowerId,
        HumanBodyBones endId,
        int rootLandmark,
        int endLandmark,
        Vector3 poleInBody,
        bool isArm)
    {
        int upperIndex = FindLimbIndex(upperId);
        int lowerIndex = FindLimbIndex(lowerId);
        Transform end = targetAnimator.GetBoneTransform(endId);

        if (upperIndex < 0 || lowerIndex < 0 || end == null)
        {
            return;
        }

        LimbBone upper = limbBones[upperIndex];
        LimbBone lower = limbBones[lowerIndex];

        upper.UsesIk = true;
        lower.UsesIk = true;

        limbBones[upperIndex] = upper;
        limbBones[lowerIndex] = lower;

        ikChains.Add(new IkChain
        {
            UpperIndex = upperIndex,
            LowerIndex = lowerIndex,
            End = end,
            RootLandmark = rootLandmark,
            EndLandmark = endLandmark,
            PoleInBody = poleInBody.normalized,
            IsArm = isArm
        });
    }

    private int FindLimbIndex(HumanBodyBones boneId)
    {
        Transform bone = targetAnimator.GetBoneTransform(boneId);

        for (int i = 0; i < limbBones.Count; i++)
        {
            if (limbBones[i].Bone == bone)
            {
                return i;
            }
        }

        return -1;
    }

    private void Update()
    {
        HasValidPose = false;

        if (bodyTracker.BodyDetected)
        {
            lastBodyTime = Time.unscaledTime;
        }
        else if (IsFitted && Time.unscaledTime - lastBodyTime > FitResetSeconds)
        {
            // Someone else may be standing in front of the mirror now.
            ResetFit();
        }

        if (!ReadPose() ||
            !TryGetBodyRotations(out Quaternion pelvisDelta, out Quaternion chestDelta))
        {
            return;
        }

        UpdateFit();

        if (!IsFitted)
        {
            return;
        }

        HasValidPose = true;

        float blend = smoothing > 0f
            ? 1f - Mathf.Exp(-smoothing * Time.deltaTime)
            : 1f;

        ApplyFit();

        ApplyWorldRotation(hips, pelvisDelta * hipsRestRotation, blend);
        ApplyWorldRotation(spine, chestDelta * spineRestRotation, blend);
        ApplyWorldRotation(chest, chestDelta * chestRestRotation, blend);

        ApplyIkChains(
            pelvisDelta * restPelvisRotation,
            chestDelta * restChestRotation,
            blend
        );

        for (int i = 0; i < limbBones.Count; i++)
        {
            LimbBone limb = limbBones[i];

            // The upper and lower limb segments are solved by the IK pass above.
            if (limb.UsesIk)
            {
                continue;
            }

            if (!poseValid[limb.StartLandmark] || !poseValid[limb.EndLandmark])
            {
                continue;
            }

            Vector3 direction = pose[limb.EndLandmark] - pose[limb.StartLandmark];

            if (direction.sqrMagnitude < MinimumDirectionSqrMagnitude)
            {
                continue;
            }

            AimBone(limb.Bone, limb, direction, blend);
        }

        if (followShoulderPosition && trackingCamera != null)
        {
            FollowShoulders(blend);
        }
    }

    private bool ReadPose()
    {
        if (!bodyTracker.BodyDetected)
        {
            return false;
        }

        IReadOnlyList<Vector3> cameraSpaceLandmarks = bodyTracker.CameraSpaceLandmarks;
        IReadOnlyList<Vector3> normalizedLandmarks = bodyTracker.Landmarks;

        if (cameraSpaceLandmarks == null || cameraSpaceLandmarks.Count < LandmarkCount ||
            normalizedLandmarks == null || normalizedLandmarks.Count < LandmarkCount)
        {
            return false;
        }

        for (int i = 0; i < LandmarkCount; i++)
        {
            Vector3 landmark = cameraSpaceLandmarks[i];

            // Kinect camera space is X right, Y down, Z forward; Unity is Y up.
            pose[i] = new Vector3(landmark.x, -landmark.y, landmark.z);
            poseValid[i] = landmark.z > 0f;
            poseInFrame[i] = IsInFrame(normalizedLandmarks[i]);
        }

        return true;
    }

    private bool TryGetBodyRotations(out Quaternion pelvisDelta, out Quaternion chestDelta)
    {
        pelvisDelta = Quaternion.identity;
        chestDelta = Quaternion.identity;

        if (!poseValid[LeftHipLandmark] || !poseValid[RightHipLandmark] ||
            !poseValid[LeftShoulderLandmark] || !poseValid[RightShoulderLandmark])
        {
            return false;
        }

        Vector3 hipRight = pose[RightHipLandmark] - pose[LeftHipLandmark];
        Vector3 shoulderRight =
            pose[RightShoulderLandmark] - pose[LeftShoulderLandmark];
        Vector3 torsoUp =
            (pose[LeftShoulderLandmark] + pose[RightShoulderLandmark]) * 0.5f -
            (pose[LeftHipLandmark] + pose[RightHipLandmark]) * 0.5f;

        if (torsoUp.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            return false;
        }

        if (!TryBuildBodyRotation(hipRight, torsoUp, out Quaternion pelvisRotation) ||
            !TryBuildBodyRotation(shoulderRight, torsoUp, out Quaternion chestRotation))
        {
            return false;
        }

        pelvisDelta = pelvisRotation * Quaternion.Inverse(restPelvisRotation);
        chestDelta = chestRotation * Quaternion.Inverse(restChestRotation);

        return true;
    }

    // Builds the world rotation of a body frame whose X axis is "right" and Y
    // axis is "up". The frame's Z axis is their cross product, i.e. forward.
    private static bool TryBuildBodyRotation(
        Vector3 right,
        Vector3 up,
        out Quaternion rotation)
    {
        rotation = Quaternion.identity;

        right.Normalize();
        up.Normalize();

        Vector3 forward = Vector3.Cross(right, up);

        if (forward.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            return false;
        }

        rotation = Quaternion.LookRotation(forward, up);

        return true;
    }

    private static void ApplyWorldRotation(
        Transform bone,
        Quaternion targetWorldRotation,
        float blend)
    {
        Quaternion targetLocalRotation =
            Quaternion.Inverse(bone.parent.rotation) * targetWorldRotation;

        bone.localRotation = Quaternion.Slerp(
            bone.localRotation,
            targetLocalRotation,
            blend
        );
    }

    private void ApplyIkChains(
        Quaternion pelvisBodyRotation,
        Quaternion chestBodyRotation,
        float blend)
    {
        for (int i = 0; i < ikChains.Count; i++)
        {
            IkChain chain = ikChains[i];

            if (!poseValid[chain.RootLandmark] || !poseValid[chain.EndLandmark])
            {
                continue;
            }

            LimbBone upper = limbBones[chain.UpperIndex];
            LimbBone lower = limbBones[chain.LowerIndex];

            float upperLength = Vector3.Distance(
                upper.Bone.position,
                lower.Bone.position
            );
            float lowerLength = Vector3.Distance(
                lower.Bone.position,
                chain.End.position
            );

            if (upperLength <= MinimumLimbLength ||
                lowerLength <= MinimumLimbLength)
            {
                continue;
            }

            Vector3 shoulderToWrist =
                pose[chain.EndLandmark] - pose[chain.RootLandmark];

            Vector3 target = upper.Bone.position + shoulderToWrist * positionScale;

            Quaternion bodyRotation = chain.IsArm
                ? chestBodyRotation
                : pelvisBodyRotation;

            Vector3 pole = bodyRotation * chain.PoleInBody;

            // The tracked elbow picks the plane the arm folds in. It is used as
            // a direction only, so the hand still lands on the tracked wrist and
            // the arm keeps its fitted length, but the sleeve bends where the
            // real elbow bends instead of wherever a fixed pole points it. A
            // nearly straight arm has no dependable bend direction, and an elbow
            // off the edge of the frame is extrapolated, so both keep the pole.
            if (chain.IsArm && poseInFrame[upper.EndLandmark])
            {
                Vector3 shoulderToElbow =
                    pose[upper.EndLandmark] - pose[chain.RootLandmark];
                Vector3 bend = shoulderToElbow -
                    Vector3.Project(shoulderToElbow, shoulderToWrist);

                if (bend.sqrMagnitude > MinimumBendSqrMagnitude)
                {
                    pole = bend;
                }
            }

            SolveTwoBone(
                upper.Bone.position,
                target,
                upperLength,
                lowerLength,
                pole,
                out Vector3 elbow,
                out Vector3 end
            );

            AimBone(upper.Bone, upper, elbow - upper.Bone.position, blend);
            AimBone(lower.Bone, lower, end - lower.Bone.position, blend);
        }
    }

    private static void AimBone(
        Transform bone,
        LimbBone limb,
        Vector3 worldDirection,
        float blend)
    {
        if (worldDirection.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            return;
        }

        Vector3 targetParentDirection =
            Quaternion.Inverse(bone.parent.rotation) * worldDirection.normalized;

        Quaternion targetLocalRotation =
            Quaternion.FromToRotation(
                limb.RestDirectionInParent,
                targetParentDirection) *
            limb.RestLocalRotation;

        bone.localRotation = Quaternion.Slerp(
            bone.localRotation,
            targetLocalRotation,
            blend
        );
    }

    // Places the elbow (or knee) on the circle where the two bones can meet,
    // on the side of the pole, then clamps the end target to the reachable
    // range so an odd depth sample cannot hyperextend the limb.
    private static void SolveTwoBone(
        Vector3 root,
        Vector3 target,
        float upperLength,
        float lowerLength,
        Vector3 poleDirection,
        out Vector3 elbow,
        out Vector3 end)
    {
        Vector3 toTarget = target - root;

        float minimumReach = Mathf.Abs(upperLength - lowerLength) + MinimumLimbLength;
        float maximumReach = upperLength + lowerLength - MinimumLimbLength;
        float distance = Mathf.Clamp(toTarget.magnitude, minimumReach, maximumReach);

        Vector3 direction = toTarget.sqrMagnitude > MinimumDirectionSqrMagnitude
            ? toTarget.normalized
            : poleDirection;

        end = root + direction * distance;

        float along =
            (upperLength * upperLength - lowerLength * lowerLength +
                distance * distance) /
            (2f * distance);
        float height = Mathf.Sqrt(Mathf.Max(
            0f,
            upperLength * upperLength - along * along
        ));

        Vector3 pole =
            poleDirection - Vector3.Dot(poleDirection, direction) * direction;

        if (pole.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            pole = Vector3.Cross(direction, Vector3.up);
        }

        if (pole.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            pole = Vector3.Cross(direction, Vector3.right);
        }

        elbow = root + direction * along + pole.normalized * height;
    }

    private void FollowShoulders(float blend)
    {
        if (!TryGetJointMid(
                LeftShoulderLandmark,
                RightShoulderLandmark,
                out Vector3 trackedShoulders))
        {
            return;
        }

        if (trackedShoulders.z > 0f && trackedShoulders.z < minimumDistance)
        {
            trackedShoulders *= minimumDistance / trackedShoulders.z;
        }

        // The landmarks sit on the person's surface, so push the avatar back by
        // half its width. Scaling X and Y with Z moves it along the camera ray,
        // which keeps the projected shoulders in place; moving straight back
        // would let perspective drag the model down the screen.
        float halfWidth =
            Vector3.Distance(leftShoulder.position, rightShoulder.position) * 0.5f;

        Vector3 shouldersInView = trackedShoulders * positionScale;
        float depth = shouldersInView.z;

        if (depth > 0f)
        {
            shouldersInView *= (depth + halfWidth) / depth;
        }

        Vector3 targetPosition = trackingCamera.TransformPoint(shouldersInView);
        Vector3 avatarShoulders =
            (leftShoulder.position + rightShoulder.position) * 0.5f;
        Vector3 error = targetPosition - avatarShoulders;

        if (!hasPlacedRoot)
        {
            targetAnimator.transform.position += error;
            hasPlacedRoot = true;
            return;
        }

        targetAnimator.transform.position += error * blend;
    }

    // Records the avatar's own segment sizes, which the tracked body is matched
    // against. Measuring the skeleton instead of assuming a size keeps the fit
    // correct for any rig and any import scale.
    private bool CacheFitReferences()
    {
        authoredScale = targetAnimator.transform.localScale;

        Vector3 hipMid = (leftHip.position + rightHip.position) * 0.5f;
        Vector3 shoulderMid = (leftShoulder.position + rightShoulder.position) * 0.5f;

        restTorsoLength = Vector3.Distance(hipMid, shoulderMid);
        restShoulderWidth = Vector3.Distance(
            leftShoulder.position,
            rightShoulder.position
        );

        if (restTorsoLength <= 0f || restShoulderWidth <= 0f)
        {
            Debug.LogError(
                "HumanoidPoseDriver: the avatar has no measurable torso or shoulder width.",
                this
            );

            return false;
        }

        return true;
    }

    // Measures every tracked segment and moves each ratio toward it, until the
    // proportions are locked. The torso is the reference: a segment that
    // measures far outside the torso's scale is a bad depth sample, so it keeps
    // the last good value instead of collapsing the avatar.
    private void UpdateFit()
    {
        if (fitLocked)
        {
            return;
        }

        if (!TryMeasureTorso(out float torsoLength))
        {
            return;
        }

        float measuredTorsoRatio = torsoLength * positionScale / restTorsoLength;

        if (hasTorsoRatio &&
            (measuredTorsoRatio < torsoRatio * MinimumProportionRatio ||
             measuredTorsoRatio > torsoRatio * MaximumProportionRatio))
        {
            return;
        }

        float blend = 1f - Mathf.Exp(-Time.deltaTime / FitSmoothingSeconds);

        torsoRatio = hasTorsoRatio
            ? Mathf.Lerp(torsoRatio, measuredTorsoRatio, blend)
            : measuredTorsoRatio;

        hasTorsoRatio = true;

        if (TryMeasureLimb(
                LeftShoulderLandmark,
                RightShoulderLandmark,
                out float shoulderWidth))
        {
            UpdateBoneRatio(
                shoulderWidth * positionScale / restShoulderWidth,
                torsoRatio,
                blend,
                ref shoulderRatio,
                ref hasShoulderRatio
            );
        }

        if (!hasShoulderRatio)
        {
            shoulderRatio = torsoRatio;
            hasShoulderRatio = true;
        }

        for (int i = 0; i < limbBones.Count; i++)
        {
            LimbBone limb = limbBones[i];

            if (limb.MatchesLength)
            {
                if (TryMeasureLimb(limb.StartLandmark, limb.EndLandmark, out float length))
                {
                    UpdateBoneRatio(
                        length * positionScale / limb.RestLength,
                        torsoRatio,
                        blend,
                        ref limb.SmoothedRatio,
                        ref limb.HasRatio
                    );
                }

                if (!limb.HasRatio)
                {
                    limb.SmoothedRatio = torsoRatio;
                    limb.HasRatio = true;
                }
            }

            limbBones[i] = limb;
        }

        if (!IsFitted)
        {
            IsFitted = true;

            Debug.Log(
                $"HumanoidPoseDriver: fitted the avatar to a {torsoLength:F2} m torso.",
                this
            );
        }

        fitMeasuredSeconds += Time.deltaTime;

        if (fitMeasuredSeconds >= fitLockSeconds)
        {
            LockFit();
        }
    }

    private void LockFit()
    {
        fitLocked = true;

        Debug.Log(
            $"HumanoidPoseDriver: locked the fit at torso {torsoRatio:F2}x, " +
            $"shoulders {shoulderRatio:F2}x.",
            this
        );
    }

    private static void UpdateBoneRatio(
        float measured,
        float reference,
        float blend,
        ref float ratio,
        ref bool hasRatio)
    {
        if (measured < reference * MinimumProportionRatio ||
            measured > reference * MaximumProportionRatio)
        {
            return;
        }

        ratio = hasRatio ? Mathf.Lerp(ratio, measured, blend) : measured;
        hasRatio = true;
    }

    // Applies the per-bone fit. The root carries the torso scale, the clavicles
    // carry the shoulder width, and each limb bone is compensated for whatever
    // scale it inherited so its own segment ends up at its measured length.
    private void ApplyFit()
    {
        targetAnimator.transform.localScale = authoredScale * torsoRatio;

        if (leftClavicle != null && rightClavicle != null)
        {
            ApplyBoneScale(leftClavicle, leftClavicleRestScale, shoulderRatio);
            ApplyBoneScale(rightClavicle, rightClavicleRestScale, shoulderRatio);
        }

        for (int i = 0; i < limbBones.Count; i++)
        {
            LimbBone limb = limbBones[i];

            if (limb.MatchesLength)
            {
                ApplyBoneScale(limb.Bone, limb.RestLocalScale, limb.SmoothedRatio);
            }
        }
    }

    private void ApplyBoneScale(Transform bone, Vector3 restLocalScale, float ratio)
    {
        float parentScale = bone.parent.lossyScale.x;

        bone.localScale = restLocalScale * (authoredScale.x * ratio / parentScale);
    }

    private void ResetFit()
    {
        IsFitted = false;
        fitLocked = false;
        fitMeasuredSeconds = 0f;
        hasPlacedRoot = false;
        hasTorsoRatio = false;
        hasShoulderRatio = false;

        for (int i = 0; i < limbBones.Count; i++)
        {
            LimbBone limb = limbBones[i];
            limb.HasRatio = false;
            limbBones[i] = limb;
        }
    }

    private bool TryMeasureTorso(out float length)
    {
        length = 0f;

        if (!TryGetJointMid(LeftHipLandmark, RightHipLandmark, out Vector3 hipMid) ||
            !TryGetJointMid(
                LeftShoulderLandmark,
                RightShoulderLandmark,
                out Vector3 shoulderMid) ||
            Mathf.Abs(hipMid.z - shoulderMid.z) > MaximumDepthSpread)
        {
            return false;
        }

        length = Vector3.Distance(hipMid, shoulderMid);

        return true;
    }

    private bool TryMeasureLimb(int startLandmark, int endLandmark, out float length)
    {
        length = 0f;

        if (!poseValid[startLandmark] || !poseValid[endLandmark] ||
            !poseInFrame[startLandmark] || !poseInFrame[endLandmark])
        {
            return false;
        }

        Vector3 start = pose[startLandmark];
        Vector3 end = pose[endLandmark];

        // A landmark depth that landed on the background would fake a much
        // longer or shorter segment.
        if (Mathf.Abs(start.z - end.z) > MaximumDepthSpread)
        {
            return false;
        }

        length = Vector3.Distance(start, end);

        return length > 0f &&
            Mathf.Abs(start.z - end.z) <= length * MaximumForeshorteningRatio;
    }

    private bool TryGetJointMid(int leftLandmark, int rightLandmark, out Vector3 mid)
    {
        mid = Vector3.zero;

        if (!poseValid[leftLandmark] || !poseValid[rightLandmark] ||
            !poseInFrame[leftLandmark] || !poseInFrame[rightLandmark] ||
            Mathf.Abs(pose[leftLandmark].z - pose[rightLandmark].z) > MaximumDepthSpread)
        {
            return false;
        }

        mid = (pose[leftLandmark] + pose[rightLandmark]) * 0.5f;

        return true;
    }

    private static bool IsInFrame(Vector3 normalized)
    {
        return normalized.x >= 0f && normalized.x <= 1f &&
            normalized.y >= 0f && normalized.y <= 1f;
    }
}
