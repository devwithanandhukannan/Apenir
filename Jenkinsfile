pipeline {
    agent any

    environment {
        DOCKER_HUB_USER = "kneazllle"
        IMAGE_NAME      = "apenir-backend"
        EC2_PUBLIC_IP   = "54.85.138.131"
    }

    stages {

        stage('Checkout Code') {
            steps {
                checkout scm
            }
        }

        stage('Build & Push Docker Image') {
            steps {
                echo 'Building Docker image for AMD64...'

                withCredentials([
                    usernamePassword(
                        credentialsId: 'docker-hub-credentials',
                        usernameVariable: 'USER',
                        passwordVariable: 'PASS'
                    )
                ]) {

                    sh '''
                        echo ${PASS} | docker login -u ${USER} --password-stdin

                        docker build \
                            --platform linux/amd64 \
                            -t ${DOCKER_HUB_USER}/${IMAGE_NAME}:latest .

                        docker push ${DOCKER_HUB_USER}/${IMAGE_NAME}:latest
                    '''
                }
            }
        }

        stage('Deploy to EC2') {
            steps {

                withCredentials([
                    sshUserPrivateKey(
                        credentialsId: 'ec2-ssh-key',
                        keyFileVariable: 'KEY_FILE'
                    )
                ]) {

                    sh """
                    ssh -o StrictHostKeyChecking=no -i ${KEY_FILE} ec2-user@${EC2_PUBLIC_IP} '

                        echo "Pulling latest image..."
                        docker pull ${DOCKER_HUB_USER}/${IMAGE_NAME}:latest

                        echo "Stopping old container..."
                        docker stop dotnet-app || true
                        docker rm dotnet-app || true

                        echo "Starting new container..."
                        docker run -d \
                            --name dotnet-app \
                            --restart unless-stopped \
                            -p 5000:8080 \
                            ${DOCKER_HUB_USER}/${IMAGE_NAME}:latest

                        echo "Deployment completed."

                    '
                    """
                }
            }
        }
    }

    post {
        always {
            echo 'Logging out from Docker Hub...'
            sh 'docker logout || true'
        }

        success {
            echo 'Deployment Successful!'
        }

        failure {
            echo 'Deployment Failed!'
        }
    }
}
