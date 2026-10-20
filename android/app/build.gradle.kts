import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

// Release signing comes from android/keystore.properties (gitignored), which names the upload
// keystore and its passwords - see docs/STORE_DISTRIBUTION.md. Absent, a release build is still
// produced but signed with the debug key: fine for a local install, refused by Google Play.
val keystorePropertiesFile = rootProject.file("keystore.properties")
val keystoreProperties = Properties().apply {
    if (keystorePropertiesFile.exists()) keystorePropertiesFile.inputStream().use { load(it) }
}
val hasReleaseKeystore = keystorePropertiesFile.exists()

if (file("google-services.json").exists()) {
    apply(plugin = "com.google.gms.google-services")
}

android {
    namespace = "com.sentinelcore.android"
    // Google Play requires new apps to target API 36 (Android 16) from 31 Aug 2026; compileSdk
    // follows so the code is checked against the SDK it declares support for.
    compileSdk = 36

    defaultConfig {
        applicationId = "com.sentinelcore.android"
        minSdk = 26
        targetSdk = 36
        versionCode = 39
        versionName = "0.1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        vectorDrawables.useSupportLibrary = true
    }

    signingConfigs {
        if (hasReleaseKeystore) {
            create("release") {
                storeFile = rootProject.file(keystoreProperties.getProperty("storeFile"))
                storePassword = keystoreProperties.getProperty("storePassword")
                keyAlias = keystoreProperties.getProperty("keyAlias")
                keyPassword = keystoreProperties.getProperty("keyPassword")
            }
        }
    }
    buildTypes {
        // MEDIA_DOWNLOADS: the torrent downloads behind Movies' and Music's "+ Add", Import a
        // list, search's download offer and Suggested for you. Google Play's policy bars apps that
        // fetch films and albums from torrent sites, so release builds (the Play uploads) never
        // ask the server about downloads and those screens stay hidden; the dashboard keeps them.
        debug {
            buildConfigField("boolean", "MEDIA_DOWNLOADS", "true")
        }
        release {
            buildConfigField("boolean", "MEDIA_DOWNLOADS", "false")
            if (hasReleaseKeystore) signingConfig = signingConfigs.getByName("release")
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    testOptions {
        // Robolectric (the Android Auto car-app tests in car/) reads the merged manifest and
        // resources.
        unitTests.isIncludeAndroidResources = true
        // The Organizer's real-server tests (OrganizerLiveServerTest) find the throwaway servers
        // scripts/with-organizer-servers.sh starts through these; plain test runs skip them.
        unitTests.all { test ->
            listOf("ORGANIZER_TEST_SERVER_A", "ORGANIZER_TEST_SERVER_B").forEach { name ->
                System.getenv(name)?.let { test.environment(name, it) }
            }
        }
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.10.01")
    implementation(composeBom)
    androidTestImplementation(composeBom)
    // On-device tests (src/androidTest): Android Auto's media browsing, driven through the
    // platform MediaBrowser against an in-process stub server.
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")

    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.navigation:navigation-compose:2.8.5")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")

    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.core:core-splashscreen:1.0.1")
    implementation("com.google.firebase:firebase-messaging:25.1.1")
    // Home-screen widgets for the Organizer (widgets/OrganizerWidgets).
    implementation("androidx.glance:glance-appwidget:1.1.1")
    implementation("androidx.glance:glance-material3:1.1.1")
    // Syncing the Organizer in the background so the widgets stay current (widgets/OrganizerBackgroundSync).
    implementation("androidx.work:work-runtime-ktx:2.9.1")

    implementation("androidx.media3:media3-exoplayer:1.5.1")
    implementation("androidx.media3:media3-exoplayer-hls:1.5.1")
    implementation("androidx.media3:media3-ui:1.5.1")
    // Music & Podcasts (core/docs/MUSIC_AND_PODCASTS.md): the shared audio player runs in a
    // MediaLibraryService, which gives the media notification, lock screen, Bluetooth buttons and
    // Android Auto's browsable library. See audio/AudioPlaybackService.
    implementation("androidx.media3:media3-session:1.5.1")
    // The session's callbacks answer with ListenableFutures; this turns a coroutine into one.
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-guava:1.9.0")
    // Android Auto's IoT category (car/): the alarm, Device Hub switches, scenes and recent alerts
    // on the car screen. Music already reaches the car through the MediaLibraryService above.
    implementation("androidx.car.app:app:1.7.0")
    implementation("androidx.car.app:app-projected:1.7.0")

    // Using this phone as one of the dashboard's cameras: its video is pushed to the server over
    // WebRTC, the same way the web dashboard's publish page does it. See DevicePublisher.
    implementation("io.getstream:stream-webrtc-android:1.3.8")

    debugImplementation("androidx.compose.ui:ui-tooling")
    debugImplementation("androidx.compose.ui:ui-test-manifest")

    testImplementation("junit:junit:4.13.2")
    // android.jar's org.json is a stub that throws on the JVM; the Organizer's sync code speaks
    // JSON throughout, so its unit tests need the real thing.
    testImplementation("org.json:json:20240303")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.9.0")
    // Android Auto without a car: the Car App Library's own test host (TestCarContext,
    // ScreenController) running the car screens on Robolectric, against a stub server.
    testImplementation("androidx.car.app:app-testing:1.7.0")
    testImplementation("org.robolectric:robolectric:4.14.1")
    testImplementation("androidx.test:core:1.6.1")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
}
