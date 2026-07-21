# Doesn't changing fan speed every second wear the fans out?

No — the fans aren't being jerked, they're being nudged, and nudging is what fans
are built for. Here's how it actually works:

**1. A degree isn't a jerk — it's 1%.**
The rule is 1°C = 1% fan speed. On a ~1500 RPM fan, 1% is ~15 RPM. That's not a
speed change you can hear or feel — it's less variation than the fan's own bearing
wobble.

**2. Changes are rate-limited, not instant.**
The app checks once a second, but the fans are only *allowed* to move **8%/sec up,
3%/sec down**. Even if the GPU spikes 30 degrees, the fans glide up over several
seconds — and coast down even slower. There is physically no code path that slams
a fan from 40% to 90% in one step.

**3. There's nothing mechanical to wear out in a speed change.**
Fan speed is set by **PWM** — an electronic signal telling the motor how much power
to use. No clutch, no gears, no contact parts. Changing it is like a dimmer switch
on a light, not shifting a transmission. The motor just receives slightly more or
less push.

**4. This is exactly what the motherboard does anyway.**
Every BIOS fan curve on earth adjusts fans continuously with temperature — that's
what "fan curve" means. The app isn't doing something unusual to the fans; it's
doing the *same thing* the board does, with a nicer rule.

**5. What actually kills fans, the app avoids.**
Fans die from bearing age, dust, heat — and the one semi-hard event, **stop/start
cycling**. That's why the app has a hard 30% floor: a fan under its control never
stalls and never has to kick itself back up from zero.

**6. And in practice, they mostly just sit there.**
Temps at idle barely move, so the fans hold one speed for minutes at a time. Watch
the app for a while: the % creeps by a point or two occasionally. That's the
reality behind the "every second" — most seconds, nothing changes.

---

**The short version:** *It's a dimmer switch, not a gear shift — electronic,
gradual, rate-limited, and it never lets a fan stall. It's the same thing your
motherboard does all day, just smarter.*
