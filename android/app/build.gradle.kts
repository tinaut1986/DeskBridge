plugins {
    id("com.android.application")
}

android {
    namespace = "com.deskbridge.app"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.deskbridge.app"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    implementation(project(":protocol"))
    // Google's QR scanner (Play services): no camera permission, the scanning screen is Google's.
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")
}
