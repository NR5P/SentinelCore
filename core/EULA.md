# Sentinel Core — End User License Agreement

**Effective date:** [DATE]
**Licensor:** [LEGAL ENTITY NAME], a [STATE] limited liability company ("Licensor", "we", "us")
**Contact:** [SUPPORT EMAIL]

PLEASE READ THIS AGREEMENT CAREFULLY. BY DOWNLOADING, INSTALLING, OR USING SENTINEL
CORE, YOU AGREE TO BE BOUND BY IT. IF YOU DO NOT AGREE, DO NOT INSTALL OR USE THE
SOFTWARE.

SECTION 9 (SECURITY DISCLAIMER), SECTION 10 (BIOMETRIC AND RECORDING COMPLIANCE),
SECTION 12 (DISCLAIMER OF WARRANTIES) AND SECTION 13 (LIMITATION OF LIABILITY)
CONTAIN IMPORTANT LIMITATIONS ON OUR OBLIGATIONS TO YOU.

---

## 1. Definitions

**"Software"** means the Sentinel Core server application, its bundled companion
components (including the HomeKit bridge sidecar), the iOS, Android and Apple TV client
applications, the web dashboard, documentation, and any updates we make available.

**"You"** means the individual or legal entity that installs or uses the Software.

**"Installation"** means one running instance of the Sentinel Core server on one machine.

**"License Key"** means the signed `license.lic` file we issue to you.

**"Unlicensed Mode"** means operation of the Software without a valid License Key.

**"Licensed Mode"** means operation of the Software with a valid License Key present.

---

## 2. License Grant

Subject to your compliance with this Agreement, we grant you a non-exclusive,
non-transferable, worldwide license to install and use the Software.

**2.1 Unlicensed Mode.** You may download, install and use the Software in Unlicensed
Mode free of charge, for any purpose including commercial use, for as long as you like.
In Unlicensed Mode the Software displays a watermark over camera imagery. All other
functionality is available.

**2.2 Licensed Mode.** Upon purchase we issue you a License Key. A License Key removes
the watermark. Your License Key is perpetual: it does not expire, and it does not stop
working if you stop paying us anything or if we cease operating.

**2.3 Scope.** A License Key is issued to you as a named customer and authorizes use
across Installations that you own or operate. It does not authorize use by a separate
legal entity, nor use on behalf of third parties as a service, without a separate
agreement with us.

**2.4 Updates.** Your License Key is valid for all versions of the Software released
during the [MAJOR VERSION, e.g. "1.x"] series. Whether it extends to subsequent major
versions is stated at the time that major version is released. We are not obligated to
release updates.

---

## 3. Restrictions

You may not:

**(a)** remove, disable, bypass, or interfere with the watermark, the License Key check,
or any part of the licensing mechanism, or use the Software in a manner that produces the
effect of Licensed Mode without a valid License Key;

**(b)** distribute, publish, sublicense, sell, rent, lease, or lend the Software or your
License Key, or make either available to anyone else;

**(c)** reverse engineer, decompile, or disassemble the Software, except to the extent
that applicable law expressly permits this notwithstanding this limitation;

**(d)** modify or create derivative works of the Software;

**(e)** remove or obscure any proprietary notice, including the third-party notices file
distributed with the Software.

**3.1 Anti-circumvention.** The licensing mechanism described in Section 2 is a
technological measure that controls access to a copyrighted work. You agree not to
circumvent it, and not to manufacture, distribute, or traffic in any means of
circumventing it. This obligation is in addition to, and does not limit, any rights we
have under 17 U.S.C. § 1201 or equivalent laws.

---

## 4. Ownership

The Software is licensed, not sold. We and our licensors retain all right, title and
interest in the Software, including all intellectual property rights. No rights are
granted except as expressly set out in this Agreement.

---

## 5. Third-Party Components

The Software includes third-party components licensed under their own terms, identified
in the `THIRD-PARTY-NOTICES.txt` file distributed with the Software. Those terms govern
those components. Where a third-party license grants you rights broader than this
Agreement with respect to that component, those rights control for that component.

The Software also interoperates with software you install yourself — including ffmpeg,
go2rtc, and Ollama. We do not distribute those, do not license them to you, and make no
representations about them.

---

## 6. Your Data

The Software is self-hosted. Camera imagery, video, event records, face enrollment data
and embeddings, and configuration remain on infrastructure you control. We do not receive
them and have no access to them.

**6.1 What we do receive.** When you purchase a License Key, we receive the name and
email address you provide, which are embedded in the signed key. If you enable push
notifications, device push tokens are transmitted to Google Firebase Cloud Messaging to
deliver those notifications; this is governed by our privacy policy at [PRIVACY POLICY
URL].

**6.2 No telemetry.** The Software does not report usage back to us. License validation
is performed locally against a public key embedded in the Software and requires no
network connection to us.

---

## 7. Support

Support, if any, is provided as described at [SUPPORT URL]. We do not commit to any
response time or resolution unless we have separately agreed to one in writing.

---

## 8. Fees and Refunds

License Key fees are as published at the time of purchase. Refund terms are as stated at
[REFUND POLICY URL]. Because a License Key is a perpetual file that cannot be recalled
once delivered, refunds are at our discretion outside of any right you have under
mandatory consumer law.

---

## 9. Security Disclaimer

**THE SOFTWARE IS NOT A LIFE-SAFETY SYSTEM, AN ALARM SYSTEM, OR A MONITORED SECURITY
SERVICE, AND MUST NOT BE RELIED ON AS ONE.**

You acknowledge that:

**(a)** the Software depends on your cameras, your network, your power supply, your
storage, and third-party services, any of which can fail without notice;

**(b)** motion detection, object detection, face recognition and AI-generated analysis
are probabilistic and will produce both false positives and false negatives;

**(c)** notifications may be delayed, duplicated, or not delivered at all;

**(d)** recordings may be incomplete, corrupted, deleted by retention policy, or absent;

**(e)** the Software will not prevent, interrupt, or summon assistance for any burglary,
intrusion, fire, medical emergency, or other event.

You are solely responsible for maintaining appropriate insurance, alarm services, and
emergency arrangements. Do not use the Software in any application where failure could
lead to death, personal injury, or severe physical or environmental damage.

---

## 10. Biometric, Recording and Privacy Compliance

**This section is important. Read it before enabling face recognition.**

**10.1 You are the controller.** You determine what cameras exist, where they point, who
is recorded, what is retained and for how long, and whether face recognition is enabled.
For all purposes under applicable data protection and biometric privacy law, you — not
we — are the data controller and operator. We have no access to and no control over any
of it.

**10.2 Face recognition is off by default.** Face recognition is disabled unless you
enable it. If you enable it, the Software computes and stores mathematical
representations of faces ("face embeddings"). Depending on where you and the recorded
individuals are located, face embeddings may constitute **biometric identifiers** or
**special category personal data** under laws including the Illinois Biometric
Information Privacy Act (740 ILCS 14), the Texas Capture or Use of Biometric Identifier
Act, the Washington biometric privacy statute, the EU and UK General Data Protection
Regulation, and other laws.

**10.3 Your obligations.** If you enable face recognition, you are solely responsible for
determining and satisfying every legal requirement that applies to you, which may
include: providing written notice before collection; obtaining written consent from each
individual whose face is enrolled or recognized; publishing a written retention and
destruction schedule; honoring deletion and access requests; and not selling, leasing or
otherwise profiting from biometric data. Some of these obligations carry statutory
damages per violation and a private right of action.

**10.4 Audio and video recording.** Recording of video and especially audio is regulated,
and in a number of jurisdictions recording audio without the consent of all parties is a
criminal offense. You are solely responsible for the lawfulness of what you record, where
your cameras point, signage and notice requirements, and any workplace or tenancy rules
that apply.

**10.5 No legal advice.** Nothing in the Software or its documentation is legal advice.
We do not assess your deployment for compliance and do not warrant that any configuration
is lawful in your jurisdiction.

---

## 11. Indemnity

You will defend, indemnify and hold us harmless from any claim, demand, loss, liability,
damage, fine, penalty, or expense (including reasonable legal fees) arising out of or
relating to: (a) your use of the Software; (b) what you record and how you use it;
(c) your collection, storage, use, or disclosure of biometric data or other personal
data; (d) your violation of any law, including those referenced in Section 10; or
(e) your breach of this Agreement.

---

## 12. Disclaimer of Warranties

THE SOFTWARE IS PROVIDED "AS IS" AND "AS AVAILABLE", WITH ALL FAULTS AND WITHOUT WARRANTY
OF ANY KIND. TO THE MAXIMUM EXTENT PERMITTED BY LAW, WE DISCLAIM ALL WARRANTIES, EXPRESS,
IMPLIED, STATUTORY OR OTHERWISE, INCLUDING ANY IMPLIED WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE, TITLE, NON-INFRINGEMENT, ACCURACY, AND ANY WARRANTY
ARISING FROM COURSE OF DEALING OR USAGE OF TRADE.

WE DO NOT WARRANT THAT THE SOFTWARE WILL BE UNINTERRUPTED, ERROR-FREE, SECURE, OR THAT IT
WILL DETECT, RECORD, RECOGNIZE, OR NOTIFY YOU OF ANY PARTICULAR EVENT.

Some jurisdictions do not allow the exclusion of implied warranties, so some of the above
may not apply to you. In that case such warranties are limited to the shortest period
permitted by law.

---

## 13. Limitation of Liability

TO THE MAXIMUM EXTENT PERMITTED BY LAW:

**13.1** WE WILL NOT BE LIABLE FOR ANY INDIRECT, INCIDENTAL, SPECIAL, CONSEQUENTIAL,
EXEMPLARY OR PUNITIVE DAMAGES, OR FOR ANY LOSS OF PROFITS, REVENUE, DATA, RECORDINGS,
GOODWILL, OR BUSINESS INTERRUPTION, HOWEVER CAUSED AND UNDER ANY THEORY OF LIABILITY,
EVEN IF WE HAVE BEEN ADVISED OF THE POSSIBILITY.

**13.2** WE WILL NOT BE LIABLE FOR ANY LOSS, THEFT, PROPERTY DAMAGE, PERSONAL INJURY, OR
DEATH ARISING FROM ANY FAILURE OF THE SOFTWARE TO DETECT, RECORD, OR REPORT ANY EVENT.

**13.3** OUR TOTAL AGGREGATE LIABILITY ARISING OUT OF OR RELATING TO THIS AGREEMENT OR
THE SOFTWARE WILL NOT EXCEED THE GREATER OF (a) THE AMOUNT YOU ACTUALLY PAID US FOR YOUR
LICENSE KEY IN THE TWELVE MONTHS PRECEDING THE EVENT GIVING RISE TO THE CLAIM, OR
(b) FIFTY US DOLLARS ($50).

**13.4** IF YOU USE THE SOFTWARE IN UNLICENSED MODE, YOU HAVE PAID US NOTHING, AND OUR
TOTAL AGGREGATE LIABILITY TO YOU IS FIFTY US DOLLARS ($50).

**13.5** These limitations apply even if a limited remedy fails of its essential purpose,
and do not apply to liability that cannot be excluded or limited under applicable law
(which may include liability for fraud, gross negligence, or willful misconduct).

**13.6** You acknowledge that the fees for the Software reflect this allocation of risk,
that we would not offer the Software on these terms without it, and that it is a
fundamental basis of the bargain between us.

---

## 14. Term and Termination

This Agreement takes effect when you first install or use the Software and continues
until terminated.

**14.1** You may terminate at any time by ceasing use and removing the Software.

**14.2** We may terminate this Agreement if you materially breach it — in particular
Section 3 — and do not cure the breach within thirty (30) days of notice. Because License
Keys are perpetual and validated offline, termination is a legal remedy and not a
technical one.

**14.3** Sections 4, 5, 6, 9, 10, 11, 12, 13, 15 and 16 survive termination.

---

## 15. Export and Government Use

You may not use or export the Software in violation of United States export laws or the
laws of your jurisdiction, or provide it to any party subject to applicable sanctions or
denied-party lists. If licensed to a U.S. Government entity, the Software is "commercial
computer software" under FAR 12.212 and DFARS 227.7202, licensed with only the rights
granted here.

---

## 16. General

**16.1 Governing law.** This Agreement is governed by the laws of the State of [STATE],
excluding its conflict-of-laws rules and the U.N. Convention on Contracts for the
International Sale of Goods. The exclusive venue for disputes is the state and federal
courts located in [COUNTY, STATE], and both parties consent to personal jurisdiction
there. Nothing here deprives a consumer of the protection of mandatory law in their
country of residence.

**16.2 Assignment.** You may not assign this Agreement without our prior written consent.
We may assign it in connection with a merger, acquisition, or sale of assets.

**16.3 Severability.** If any provision is held unenforceable, it will be modified to the
minimum extent necessary to make it enforceable, and the remainder stays in force.

**16.4 No waiver.** Our failure to enforce any provision is not a waiver of it.

**16.5 Changes.** We may change this Agreement for future versions of the Software. The
version distributed with the version of the Software you install governs your use of that
version. Continuing to use a new version after a change means you accept it for that
version.

**16.6 Entire agreement.** This Agreement, together with any documents it references, is
the entire agreement between us regarding the Software, and supersedes all prior
discussions.
