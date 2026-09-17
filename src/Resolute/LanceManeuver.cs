using UnityEngine;
namespace Resolute {
 internal sealed class LanceManeuverState {
  internal Vector3 Before, AttitudeUp, UprightReference, ManeuverAxis, SteeringG, MeasuredG;
  internal float Range, TimeToTarget=60f, ManeuverCommandG, ManeuverHold, ManeuverHoldDuration;
  internal float ManeuverElapsed, ManeuverPlane, WeaveBlend, BankRollRate, LeadError, CommandG, CommandAlignment, RollError, SteeringStallAge;
  internal bool Evasive, Warning, ManeuverInitialized, ManeuverOpposite, GuidancePriority, BankDemandActive, UprightPoleHold;
  internal bool Diving, TerminalRecovery;
  internal float Depression, AvailableG=39f, RecoveryHorizon, RecoveryDemandG;
  internal float DiveStartRange, DiveStartHeight, DiveStartSlope;
  internal int ManeuverTimeouts;
  internal string Motion;
  internal uint Seed=1;
  internal float NextRandom(){Seed^=Seed<<13;Seed^=Seed>>17;Seed^=Seed<<5;return(Seed&0x00ffffffu)/16777216f;}
 }

 internal static class LanceManeuver {
  internal static void PrepareManeuver(LanceManeuverState s,Vector3 objective,float dt) {
   s.ManeuverCommandG=0f;
   if(!s.Evasive||s.TerminalRecovery)return;
   float rangeNm=s.Range/1852f;
   float urgency=Mathf.SmoothStep(0f,1f,Mathf.InverseLerp(450f,100f,rangeNm));
   if(s.Warning)urgency=1f;
   if(!s.ManeuverInitialized)BeginPair(s);
   else if(s.ManeuverHold>=s.ManeuverHoldDuration||s.ManeuverElapsed>=2.4f) {
    if(s.ManeuverElapsed>=2.4f) {
     // A failed alignment is observable and cannot trap one leg indefinitely.
     // Drop accumulated roll velocity, never snap the model's orientation.
     s.ManeuverTimeouts++;s.BankRollRate=0f;
    }
    if(s.ManeuverOpposite)BeginPair(s);
    else {s.ManeuverOpposite=true;s.ManeuverHold=s.ManeuverElapsed=0f;}
   }
   s.WeaveBlend=Mathf.MoveTowards(s.WeaveBlend,1f,dt);
   float cruise=Mathf.Lerp(12f,40f,urgency);
   // Keep full loaded pulls until the physical recovery boundary is reached.
   // Range urgency is retained; there is no fixed 12/5-second terminal fade.
   s.ManeuverCommandG=cruise*Mathf.SmoothStep(0f,1f,s.WeaveBlend);
   Vector3 up=PlaneUp(Vector3.up,objective,s.AttitudeUp);
   Vector3 right=Vector3.Cross(up,objective).normalized;
   float sign=s.ManeuverOpposite?-1f:1f;
   s.ManeuverAxis=(right*Mathf.Cos(s.ManeuverPlane)+up*Mathf.Sin(s.ManeuverPlane))*sign;
  }
  static void BeginPair(LanceManeuverState s) {
   int quadrant=Mathf.Min(3,(int)(s.NextRandom()*4f));
   s.ManeuverPlane=(45f+quadrant*90f+(s.NextRandom()*40f-20f))*Mathf.Deg2Rad;
   // Keep a discernible loaded pull at every range. Only time producing the
   // requested aligned force counts; roll-in time is not a successful hold.
   // Urgency still changes demand, not this 0.50..0.70-second hold interval.
   s.ManeuverHoldDuration=.50f+s.NextRandom()*.20f;
   s.ManeuverHold=s.ManeuverElapsed=0f;s.ManeuverOpposite=false;s.ManeuverInitialized=true;
  }
  internal static Vector3 Turn(LanceManeuverState s,Vector3 objective,float speed,float dt) {
   Vector3 transverse=Vector3.ProjectOnPlane(objective,s.Before);
   float dot=Mathf.Clamp(Vector3.Dot(s.Before,objective),-1f,1f);
   float angle=Mathf.Atan2(transverse.magnitude,dot);
   if(transverse.sqrMagnitude<.0000001f&&dot<0f)transverse=PlaneUp(s.AttitudeUp,s.Before,Vector3.right);
   float response=Mathf.Lerp(.20f,.85f,Mathf.Clamp01(s.TimeToTarget/6f));
   float guidanceDemand=Mathf.Min(40f,angle*speed/(9.80665f*response));
   Vector3 guidance=transverse.sqrMagnitude>.0000001f?transverse.normalized*guidanceDemand:Vector3.zero;
   s.LeadError=angle*Mathf.Rad2Deg;
   float entryError=s.TerminalRecovery?3f:12f;
   if(s.Diving) s.GuidancePriority=s.TerminalRecovery||guidanceDemand>=39.9f;
   else if(s.GuidancePriority) {
    if(s.LeadError<entryError*.30f) {s.GuidancePriority=false;s.ManeuverHold=s.ManeuverElapsed=0f;}
   } else if(s.LeadError>entryError)s.GuidancePriority=true;

   Vector3 maneuver=Vector3.ProjectOnPlane(s.ManeuverAxis,s.Before);
   if(maneuver.sqrMagnitude>.000001f)maneuver.Normalize();
   Vector3 command;
   bool pulse=s.Evasive&&s.ManeuverCommandG>.0001f&&!s.GuidancePriority;
   if(pulse) {
    // Do not add an opposing high-g course correction then normalize its tiny
    // remainder back to 40g: that caused07's spinning, unloaded equilibrium.
    // Bias only across the selected pulse axis, by at most atan(.25)=14deg.
    if(s.Diving) {
     // During descent reserve the actual course-correction component, then
     // spend the remaining combined forty-g budget on a perpendicular pull.
     // This keeps evasion alive without repeatedly abandoning the dive path.
     Vector3 lateral=guidance.sqrMagnitude>.0001f?Vector3.ProjectOnPlane(maneuver,guidance.normalized):maneuver;
     if(lateral.sqrMagnitude<.0001f)lateral=Vector3.Cross(s.Before,guidance)*(s.ManeuverOpposite?-1f:1f);
     float residual=Mathf.Sqrt(Mathf.Max(0f,s.ManeuverCommandG*s.ManeuverCommandG-guidance.sqrMagnitude));
     command=guidance+lateral.normalized*residual;
    } else {
     Vector3 across=Vector3.ProjectOnPlane(guidance,maneuver);
     across=Vector3.ClampMagnitude(across/Mathf.Max(s.ManeuverCommandG,1f),.25f);
     command=(maneuver+across).normalized*s.ManeuverCommandG;
    }
    s.ManeuverElapsed+=dt;
    s.Motion="continuous-loaded-pull";
   } else {
    command=guidance;s.Motion=s.GuidancePriority?"native-lead-course-correction":"native-lead-tracking";
    if(s.Evasive&&!s.TerminalRecovery&&command.sqrMagnitude>.000001f)
     command=command.normalized*Mathf.Max(command.magnitude,s.ManeuverCommandG);
   }
   command=Vector3.ClampMagnitude(command,40f);
   s.CommandG=command.magnitude;
   Vector3 pullUp=AlignUpToPull(s,command,s.CommandG,dt);
   s.CommandAlignment=command.sqrMagnitude>.000001f?Vector3.Angle(pullUp,command):0f;
   // Below3g the game can make small stabilizing corrections without flipping
   // the body. Coupling increases smoothly to full roll-then-body-up pull at
   // 15g; strong40g dives therefore require inversion, as requested.
   float coupling=Mathf.SmoothStep(0f,1f,Mathf.InverseLerp(3f,15f,s.CommandG));
   // Roll the lift vector with the body while retaining its magnitude. A
   // reversal is a loaded bank transition, not an unloaded wait for alignment.
   float requested=s.CommandG;
   float amount=Mathf.MoveTowards(s.SteeringG.magnitude,requested,240f*dt);
   amount=Mathf.Clamp(amount,0f,40f);
   Vector3 direction=command.sqrMagnitude>.000001f?Vector3.Slerp(command.normalized,pullUp,coupling).normalized:pullUp;
   // Do not carry residual acceleration across an exactly canceled/zero
   // request. No direction exists for that residual and the real sum is zero.
   if(s.CommandG<.0001f)amount=0f;
   // Return a force demand, not a new velocity. Nuclear Option integrates
   // thrust, gravity and aerodynamic forces on its native Rigidbody.
   if(!pulse)amount=Mathf.Min(amount,angle*speed/(9.80665f*dt));
   s.SteeringG=direction*amount;
   s.AttitudeUp=PlaneUp(pullUp,s.Before,Vector3.up);
   float holdAngle=Mathf.Lerp(135f,12f,coupling),holdForce=Mathf.Lerp(.90f,.95f,coupling);
   // The descent command rotates as its course component changes. Count the
   // measured loaded bank, rather than waiting for a stationary pulse axis
   // that no longer exists. Cruise retains the original aligned-hold rule.
   bool loaded=s.Diving ? Vector3.Dot(s.MeasuredG,s.SteeringG.normalized)>=s.CommandG*.95f :
    s.CommandAlignment<=holdAngle&&s.MeasuredG.magnitude>=s.CommandG*holdForce&&Vector3.Angle(s.MeasuredG,command)<=holdAngle;
   if(pulse&&s.WeaveBlend>=.99f&&s.ManeuverCommandG>=1f&&loaded)
    s.ManeuverHold+=dt;
   if(s.CommandG>=12f&&s.MeasuredG.magnitude<s.CommandG*.25f)s.SteeringStallAge+=dt;
   else s.SteeringStallAge=0f;
   return s.SteeringG;
  }
  static Vector3 PlaneUp(Vector3 candidate,Vector3 forward,Vector3 fallback) {
   Vector3 up=Vector3.ProjectOnPlane(candidate,forward);
   if(up.sqrMagnitude<.000001f)up=Vector3.ProjectOnPlane(fallback,forward);
   if(up.sqrMagnitude<.000001f)up=Vector3.ProjectOnPlane(Vector3.right,forward);
   if(up.sqrMagnitude<.000001f)up=Vector3.ProjectOnPlane(Vector3.forward,forward);
   return up.normalized;
  }
  static Vector3 AlignUpToPull(LanceManeuverState s,Vector3 command,float strength,float dt) {
   Vector3 up=PlaneUp(s.AttitudeUp,s.Before,Vector3.up);
   Vector3 upright=StableUpright(s,s.Before,dt);
   float active=Mathf.SmoothStep(0f,1f,Mathf.InverseLerp(3f,15f,strength));
   Vector3 target=upright;
   s.BankDemandActive=strength>.0001f;
   if(s.BankDemandActive&&command.sqrMagnitude>.000001f) {
    Vector3 pull=PlaneUp(command,s.Before,up);
    float uprightAngle=Vector3.SignedAngle(upright,pull,s.Before);
    // Weak pull keeps a gentle upright preference; no preference remains at
    // full strength. Signed angle avoids a zero vector at an inverted pull.
    float weakBank=.30f*Mathf.SmoothStep(0f,1f,Mathf.Clamp01(strength/3f));
    target=Quaternion.AngleAxis(uprightAngle*Mathf.Lerp(weakBank,1f,active),s.Before)*upright;
   }
   float error=Vector3.SignedAngle(up,target,s.Before);
   if(Mathf.Abs(error)>175f) {
    float sign=Mathf.Abs(s.BankRollRate)>1f?Mathf.Sign(s.BankRollRate):(s.ManeuverOpposite?1f:-1f);
    error=Mathf.Abs(error)*sign;
   }
   s.RollError=error;
   float maxRate=Mathf.Lerp(90f,360f,active),acceleration=Mathf.Lerp(360f,1440f,active);
   float demand=Mathf.Abs(error)<.6f?0f:Mathf.Clamp(error*8f,-maxRate,maxRate);
   s.BankRollRate=Mathf.MoveTowards(s.BankRollRate,demand,acceleration*dt);
   float step=s.BankRollRate*dt;
   if(step*error>=0f&&Mathf.Abs(step)>Mathf.Abs(error)){step=error;s.BankRollRate=0f;}
   return (Quaternion.AngleAxis(step,s.Before)*up).normalized;
  }
  static Vector3 StableUpright(LanceManeuverState s,Vector3 forward,float dt) {
   Vector3 prior=PlaneUp(s.UprightReference,forward,s.AttitudeUp);
   Vector3 world=Vector3.ProjectOnPlane(Vector3.up,forward);
   if(s.UprightPoleHold) {if(world.sqrMagnitude>.04f)s.UprightPoleHold=false;}
   else if(world.sqrMagnitude<.01f)s.UprightPoleHold=true;
   // Keep a transported reference close to vertical; restore the world-up
   // reference progressively after leaving the pole, never flip it in one tick.
   if(!s.UprightPoleHold) {
    float error=Vector3.SignedAngle(prior,world.normalized,forward);
    prior=Quaternion.AngleAxis(Mathf.Clamp(error,-180f*dt,180f*dt),forward)*prior;
   }
   s.UprightReference=prior.normalized;
   return s.UprightReference;
  }
 }
}
