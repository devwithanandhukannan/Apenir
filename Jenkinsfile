pipeline {
    agent any

    environment {
        DOCKER_HUB_USER  = "kneazllle"
        IMAGE_NAME       = "apenir-backend"
        EC2_PUBLIC_IP    = "54.85.138.131"
    }

    stages {
        stage('Checkout Code') {
            steps {
                checkout scm
            }
        }

        stage('Build & Push Docker Image (Cross-Compile for EC2)') {
            steps {
                echo 'Building production image for Intel/AMD64 target instance...'
                
                withCredentials([usernamePassword(credentialsId: 'docker-hub-credentials', usernameVariable: 'USER', passwordVariable: 'PASS')]) {
                    sh "echo \Official PASS | docker login -u \${USER} --password-stdin"
                    
                    // Crucial flag: --platform linux/amd64 compiles it cleanly for your EC2 instance from your M1 Mac
                    sh "docker build --platform linux/amd64 -t \${DOCKER_HUB_USER}/\${IMAGE_NAME}:latest ."
                    sh "docker push \${DOCKER_HUB_USER}/\${IMAGE_NAME}:latest"
                }
            }
        }

        stage('Deploy to AWS EC2 via SSH') {
            steps {
                echo 'Accessing production server to roll out container...'
                
                withCredentials([sshUserPrivateKey(credentialsId: 'ec2-ssh-key', keyFileVariable: 'KEY_FILE')]) {
                    sh """
                    ssh -o StrictHostKeyChecking=no -i \${KEY_FILE} ec2-user@\${EC2_PUBLIC_IP} '
                        # Pull down your fresh image from registry
                        docker pull kneazllle/apenir-backend:latest
                        
                        # Tear down the old app container gracefully if it exists
                        docker stop dotnet-app || true
                        docker rm dotnet-app || true
                        
                        # Spin up your fresh container on port 80 mapping to Kestrel port 8080
                        docker run -d --name dotnet-app -p 80:8080 kneazllle/apenir-backend:latest
                    '
                    """
                }
            }
        }
    }
    
    post {
        always {
            echo 'Logging out of container registry...'
            sh 'docker logout'
        }
    }
}